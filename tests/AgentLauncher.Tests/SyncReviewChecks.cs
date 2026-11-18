using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.Tests;

// A1 同步徽章状态机 / B3 会话名持久化 / B1 逐文件 diff 审阅的原生回归。
// 全部在隔离临时目录 + 真实 git init 上构造，不依赖 VPS/mutagen，不写真实用户数据（Store.Root 只用于路径断言，不落盘）。
public static class SyncReviewChecks
{
    public static async Task<int> RunAsync()
    {
        var passed=0;var failures=0;
        var temp=Path.Combine(Path.GetTempPath(),"AgentLauncher-tests-sync-review-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        void Check(string name,Action action){try{action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
        async Task CheckAsync(string name,Func<Task> action){try{await action();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
        var git=Commands.FindExe("git.exe");
        async Task Git(string cwd,params string[] args)
        {
            var result=await AgentProbe.RunAsync(new(git!,args,"exe"),cwd,TimeSpan.FromSeconds(15),64*1024);
            if(result.TimedOut||result.ExitCode!=0)throw new IOException("git "+string.Join(" ",args)+" 失败："+result.Error);
        }

        Check("UX12 A1 sync badge pure function covers live/final branches",()=>{
            SyncHealth Live(SyncHealthState state)=>new(state,"agent-launcher-live","live detail");
            var at=DateTimeOffset.UtcNow;
            SyncFinalRecord Final(string state)=>new(1,state,"终态说明",at);
            // 有活会话：无论终态记录如何，都沿用活会话评估（Ready/Syncing/Problems）。
            Assert(WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.Ready),Final(SyncFinalRecord.Problem)).State==SyncHealthState.Ready,"活会话 Ready 被终态记录覆盖");
            Assert(WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.Syncing),Final(SyncFinalRecord.Synced)).State==SyncHealthState.Syncing,"活会话 Syncing 被终态记录覆盖");
            Assert(WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.Problems),null).State==SyncHealthState.Problems,"活会话 Problems 丢失");
            // 无活会话且无记录（含 Disconnected 非终态传 null）：未同步。
            var none=WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.NotSynced),null);
            Assert(none.State==SyncHealthState.NotSynced&&none.Label=="未同步","无记录时不是未同步");
            // 无活会话且条目终态：synced→已同步，problem→同步有问题。
            var synced=WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.NotSynced),Final(SyncFinalRecord.Synced));
            Assert(synced.State==SyncHealthState.Synced&&synced.Label=="已同步"&&synced.Detail.Contains("终态说明"),"synced 终态未显示已同步");
            var problem=WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.NotSynced),Final(SyncFinalRecord.Problem));
            Assert(problem.State==SyncHealthState.SyncProblem&&problem.Label=="同步有问题","problem 终态未显示同步有问题");
            // 未知状态 fail-closed：绝不把无法识别的记录说成已同步。
            Assert(WorkspaceInsights.EvaluateSyncBadge(Live(SyncHealthState.NotSynced),Final("future-state")).State==SyncHealthState.SyncProblem,"未知终态状态被当成已同步");
            Assert(new SyncHealth(SyncHealthState.Synced,"","").Label=="已同步"&&new SyncHealth(SyncHealthState.SyncProblem,"","").Label=="同步有问题","终态标签错误");
        });

        Check("UX12 A1/B3 sync evidence records round-trip through Store atomic JSON",()=>{
            var id=Guid.NewGuid();var at=DateTimeOffset.UtcNow;
            // 路径合同：attempts/<id:N>/sync-final.json 与 sync-session.json（与 operator-release.json 同目录模式）。
            Assert(Store.SyncFinalPath(id).EndsWith(Path.Combine("attempts",id.ToString("N"),"sync-final.json"),StringComparison.Ordinal),"sync-final 路径合同变化");
            Assert(Store.SyncSessionPath(id).EndsWith(Path.Combine("attempts",id.ToString("N"),"sync-session.json"),StringComparison.Ordinal),"sync-session 路径合同变化");
            // 读写往返（隔离临时目录，不写真实 Store.Root）。
            var dir=Path.Combine(temp,"attempts",id.ToString("N"));
            var finalPath=Path.Combine(dir,"sync-final.json");var sessionPath=Path.Combine(dir,"sync-session.json");
            var final=new SyncFinalRecord(1,SyncFinalRecord.Synced,"Mutagen flush 成功，会话已终止。",at);
            var session=new SyncSessionRecord(1,"agent-launcher-"+Guid.NewGuid().ToString("N"),@"D:\work\project","/home/agent/work",at);
            Store.Save(finalPath,final);Store.Save(sessionPath,session);
            Assert(Store.Read<SyncFinalRecord>(finalPath)==final,"sync-final 记录往返不一致");
            Assert(Store.Read<SyncSessionRecord>(sessionPath)==session,"sync-session 记录往返不一致");
            var updated=final with{State=SyncFinalRecord.Problem,Detail="flush 失败"};
            Store.Save(finalPath,updated);
            Assert(Store.Read<SyncFinalRecord>(finalPath)==updated&&File.Exists(finalPath+".bak"),"终态记录更新或备份丢失");
            Assert(WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(finalPath)==updated,"证据读取器丢记录");
            // 缺失/损坏一律 null（fail-closed 按无记录处理），不抛出。
            Assert(WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(Path.Combine(dir,"missing.json"))==null,"缺失证据不是 null");
            File.WriteAllText(finalPath,"{partial");
            Assert(WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(finalPath)==null,"损坏证据未被拒绝");
            Assert(WorkspaceInsights.EvaluateSyncBadge(new(SyncHealthState.NotSynced,"",""),WorkspaceInsights.ReadSyncEvidence<SyncFinalRecord>(finalPath)).Label=="未同步","损坏证据变成了已同步");
        });

        Check("UX12 B3 EvaluateSync matches persisted session name and falls back to sync-final",()=>{
            var local=Path.Combine(temp,"b3-local");var name="agent-launcher-"+Guid.NewGuid().ToString("N");
            string Block(string sessionName,string status)=>$"Name: {sessionName}\nAlpha:\n\tURL: {local}\nBeta:\n\tURL: ssh://agent@vps.example.com:22/home/agent/work\n{status}\n";
            var watching=Block(name,"Status: Watching for changes");
            var output=watching+"--------------------------------------------------------------------------------\n"+Block("unrelated-session","Status: Watching for changes");
            // 持久化名字精确匹配（重启后 App 用 sync-session.json 的名字恢复核对）。
            var named=WorkspaceInsights.EvaluateSync(output,local,"vps.example.com","/home/agent/work",name);
            Assert(named.State==SyncHealthState.Ready&&named.SessionName==name,"持久化会话名未匹配到就绪会话");
            Assert(WorkspaceInsights.EvaluateSync(output,local,"vps.example.com","/home/agent/work","agent-launcher-missing").State==SyncHealthState.NotSynced,"不存在的会话名被匹配");
            var problems=WorkspaceInsights.EvaluateSync(Block(name,"Status: Watching for changes\nTransition problems:\n\tpermission denied"),local,"vps.example.com","/home/agent/work",name);
            Assert(problems.State==SyncHealthState.Problems,"带 Transition problems 的命名会话未判为有问题");
            // 终态后会话已终止：list 匹配不到 → 回退 A1 sync-final。
            var gone=WorkspaceInsights.EvaluateSync("",local,"vps.example.com","/home/agent/work",name);
            Assert(gone.State==SyncHealthState.NotSynced,"空 list 仍声称有会话");
            var badge=WorkspaceInsights.EvaluateSyncBadge(gone,new SyncFinalRecord(1,SyncFinalRecord.Synced,"flush 成功",DateTimeOffset.UtcNow));
            Assert(badge.State==SyncHealthState.Synced,"终止会话未回退到 sync-final");
        });

        Check("UX12 B1 diff line truncation pure function bounds single-file and total budgets",()=>{
            Assert(WorkspaceInsights.TruncateLines("a\nb\nc",3) is {Text:"a\nb\nc",Truncated:false},"恰好 3 行被误截");
            Assert(WorkspaceInsights.TruncateLines("a\nb\nc\n",3).Truncated==false,"末尾换行被多计一行");
            var cut=WorkspaceInsights.TruncateLines("a\nb\nc\nd",3);
            Assert(cut.Truncated&&cut.Text=="a\nb\nc","4 行截到 3 行错误");
            Assert(WorkspaceInsights.TruncateLines("a\r\nb\r\nc\r\nd",2) is {Text:"a\nb",Truncated:true},"CRLF 未归一化");
            Assert(WorkspaceInsights.TruncateLines("",400) is {Text:"",Truncated:false},"空 diff 被误截");
            Assert(WorkspaceInsights.TruncateLines("a",0) is {Text:"",Truncated:true},"0 行预算未截断");
            var rejected=false;try{WorkspaceInsights.TruncateLines("a",-1);}catch(ArgumentOutOfRangeException){rejected=true;}
            Assert(rejected,"负数行预算被接受");
            // 单文件 400 行与累计 2000 行共用同一纯函数。
            var many=string.Join("\n",Enumerable.Range(1,2500).Select(i=>"line "+i));
            Assert(WorkspaceInsights.TruncateLines(many,WorkspaceInsights.MaxDiffLines).Text.Split('\n').Length==400,"单文件上限不是 400 行");
            Assert(WorkspaceInsights.TruncateLines(many,2000).Text.Split('\n').Length==2000,"累计上限不是 2000 行");
        });

        Check("UX12 B1 ChangePath parses porcelain rename quoted and short lines",()=>{
            Assert(WorkspaceInsights.ChangePath("M  src/a.txt")=="src/a.txt","普通路径解析错误");
            Assert(WorkspaceInsights.ChangePath("R  old.txt -> new.txt")=="new.txt","重命名未取新路径");
            Assert(WorkspaceInsights.ChangePath("?? \"中文 文件.txt\"")=="中文 文件.txt","引号路径未去引号");
            Assert(WorkspaceInsights.ChangePath("??")=="","短行未返回空路径");
        });

        if(git is null||!File.Exists(git))
        {
            Console.WriteLine("SKIP UX12 B1 ReadDiffAsync real-git checks（未找到 Git）");
        }
        else
        {
            await CheckAsync("UX12 B1 ReadDiffAsync real git repo diff untracked staged and 400-line cap",async()=>{
                var repo=Path.Combine(temp,"b1-repo");Directory.CreateDirectory(repo);
                await Git(repo,"init","-q");
                var tracked=Path.Combine(repo,"tracked.txt");
                await File.WriteAllTextAsync(tracked,string.Join("\n",Enumerable.Range(1,500).Select(i=>"line "+i))+"\n");
                await Git(repo,"add","--","tracked.txt");
                await Git(repo,"-c","user.email=fixture@example.com","-c","user.name=fixture","commit","-qm","init");
                // 已跟踪文件大改：diff 超 400 行必须截断并提示。
                await File.WriteAllTextAsync(tracked,string.Join("\n",Enumerable.Range(1,500).Select(i=>"changed "+i))+"\n");
                var big=await WorkspaceInsights.ReadDiffAsync(repo,"tracked.txt");
                Assert(big.Ok,"大 diff 读取失败："+big.Error);
                // 截断保留前 400 行：500 行全改的 diff 先出现删除行，新增行在截断边界之外。
                Assert(big.Text.Contains("-line 1"),"diff 内容缺失");
                Assert(big.Text.Contains("已截断")&&big.Text.Split('\n').Length<=401,"400 行截断或提示缺失");
                // 未跟踪文件：提示而不是空 diff/报错。
                await File.WriteAllTextAsync(Path.Combine(repo,"new.txt"),"untracked\n");
                var untracked=await WorkspaceInsights.ReadDiffAsync(repo,"new.txt");
                Assert(untracked.Ok&&untracked.Text.Contains("未跟踪，无 diff"),"未跟踪文件提示缺失");
                // 仅暂存的改动：回退 --cached 仍能看到内容。
                var staged=Path.Combine(repo,"staged.txt");
                await File.WriteAllTextAsync(staged,"base\n");await Git(repo,"add","--","staged.txt");
                await Git(repo,"-c","user.email=fixture@example.com","-c","user.name=fixture","commit","-qm","staged");
                await File.WriteAllTextAsync(staged,"base\nstaged change\n");await Git(repo,"add","--","staged.txt");
                var stagedDiff=await WorkspaceInsights.ReadDiffAsync(repo,"staged.txt");
                Assert(stagedDiff.Ok&&stagedDiff.Text.Contains("+staged change"),"仅暂存改动未回退 --cached");
                // 干净文件：可读的「无 diff」说明。
                await File.WriteAllTextAsync(Path.Combine(repo,"clean.txt"),"clean\n");await Git(repo,"add","--","clean.txt");
                await Git(repo,"-c","user.email=fixture@example.com","-c","user.name=fixture","commit","-qm","clean");
                var cleanDiff=await WorkspaceInsights.ReadDiffAsync(repo,"clean.txt");
                Assert(cleanDiff.Ok&&cleanDiff.Text.Contains("没有可显示的 diff"),"干净文件提示缺失");
                // 非 Git 目录：fail-closed 可读错误，不抛出。
                var plain=Path.Combine(temp,"b1-plain");Directory.CreateDirectory(plain);
                var notRepo=await WorkspaceInsights.ReadDiffAsync(plain,"x.txt");
                Assert(!notRepo.Ok&&notRepo.Error.Length>0,"非 Git 目录未拒绝");
                // ReadChanges 与 ChangePath 联动：列表行能直接喂给 diff。
                var changes=WorkspaceInsights.ReadChanges(repo);
                Assert(changes.Ok&&changes.Entries.Any(e=>e.StartsWith("?? ",StringComparison.Ordinal)),"改动列表缺少未跟踪行");
                foreach(var entry in changes.Entries)Assert(WorkspaceInsights.ChangePath(entry).Length>0,"改动行解析不出文件路径");
                // 只读证明：读取 diff 前后工作区内容不变。
                Assert(await File.ReadAllTextAsync(tracked)==string.Join("\n",Enumerable.Range(1,500).Select(i=>"changed "+i))+"\n","diff 读取修改了工作区");
            });
        }

        Console.WriteLine($"RESULT {passed} passed, {failures} failed. Artifacts: {temp}");
        return failures==0?0:1;
    }
}