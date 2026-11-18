using System.Diagnostics;
using System.Globalization;
using System.Text;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class ReviewChecks
{
    public static void Run(Action<string,Action> test,string root)
    {
        var fixtureRoot=Path.Combine(root,"review-fixtures");Directory.CreateDirectory(fixtureRoot);
        var git=Commands.FindExe("git.exe")??throw new InvalidOperationException("未找到 Git，无法运行审查回归。");
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        void Throws<T>(Action action,string message) where T:Exception
        {
            try{action();}
            catch(Exception exception)
            {
                if(exception is T)return;
                throw new Exception(message+"（实际抛出 "+exception.GetType().Name+"）",exception);
            }
            throw new Exception(message+"（未抛出）");
        }
        string NewRepo(string label)
        {
            var repo=Path.Combine(fixtureRoot,label+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(repo);
            ReviewFixtureGit.Require(git,repo,"init","-q");
            ReviewFixtureGit.Require(git,repo,"config","user.name","Review Fixture");
            ReviewFixtureGit.Require(git,repo,"config","user.email","fixture@example.invalid");
            ReviewFixtureGit.Require(git,repo,"config","core.autocrlf","false");
            return repo;
        }
        void Commit(string repo,string message){ReviewFixtureGit.Require(git,repo,"add","-A");ReviewFixtureGit.Require(git,repo,"commit","-qm",message);}
        string Head(string repo)=>ReviewFixtureGit.Require(git,repo,"rev-parse","HEAD").Trim();
        string Show(string repo,string commit,string path)=>ReviewFixtureGit.Require(git,repo,"show",commit+":"+path).TrimEnd('\r','\n');
        string Status(string repo)=>ReviewFixtureGit.Require(git,repo,"status","--porcelain");

        test("S4 snapshot and diff keep literal paths and content binding",()=>
        {
            var repo=NewRepo("snapshot");
            var tracked="base 文件;.txt";var added="新 文件;x.txt";
            File.WriteAllText(Path.Combine(repo,tracked),"one\n");Commit(repo,"baseline");
            File.WriteAllText(Path.Combine(repo,tracked),"one\ntwo\n");
            File.WriteAllText(Path.Combine(repo,added),"alpha\nbeta\n");
            var service=new ReviewService();
            var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            Assert(snapshot.HeadSha.Length==40&&snapshot.BaseSha==snapshot.HeadSha,"快照未记录明确的 HEAD/基准");
            var changed=snapshot.Files.Single(file=>file.Path==tracked);
            Assert(changed.Status=="Modified"&&!changed.Untracked&&changed.ContentHash.Length==64,"已跟踪文件状态不正确");
            var untracked=snapshot.Files.Single(file=>file.Path==added);
            Assert(untracked.Untracked&&untracked.Status=="Untracked","未跟踪文件未被单独标记");
            var diff=service.DiffAsync(snapshot,tracked).GetAwaiter().GetResult();
            Assert(diff.Text.Contains("+two")&&!diff.Truncated,"带空格/中文/分号的路径未得到 literal diff");
            var newDiff=service.DiffAsync(snapshot,added).GetAwaiter().GetResult();
            Assert(newDiff.Text.Contains("+alpha")&&!newDiff.Truncated,"未跟踪文件 diff 缺失");
            File.WriteAllText(Path.Combine(repo,tracked),"one\ntwo\nthree\n");
            var later=service.CaptureAsync(repo).GetAwaiter().GetResult();
            Assert(later.WorktreeFingerprint!=snapshot.WorktreeFingerprint&&later.HeadSha==snapshot.HeadSha,"工作区内容变化未改变指纹");
        });

        test("S4 binary and oversized diffs stay bounded",()=>
        {
            var repo=NewRepo("bounded");
            var binary="large.bin";var large="large.txt";
            File.WriteAllBytes(Path.Combine(repo,binary),[0,1,2,3,4,5]);Commit(repo,"binary base");
            File.WriteAllBytes(Path.Combine(repo,binary),[0,9,8,7,6,5]);
            File.WriteAllLines(Path.Combine(repo,large),Enumerable.Range(0,5000).Select(index=>"line "+index));
            var service=new ReviewService();
            var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            Assert(snapshot.Files.Single(file=>file.Path==binary).Binary,"二进制文件未被识别");
            var binaryDiff=service.DiffAsync(snapshot,binary).GetAwaiter().GetResult();
            Assert(binaryDiff.Truncated&&binaryDiff.Text.Length==0&&binaryDiff.Summary.Contains("二进制"),"二进制 diff 未按摘要返回");
            var largeDiff=service.DiffAsync(snapshot,large).GetAwaiter().GetResult();
            Assert(largeDiff.Truncated&&largeDiff.Text.Length==0&&largeDiff.Summary.Contains("较大"),"过大的 diff 未保持有界摘要");
        });

        test("S4 commit uses reviewed bytes only and preserves staged and untracked work",()=>
        {
            var repo=NewRepo("commit");
            foreach(var name in new[]{"a.txt","b.txt","staged.txt"})File.WriteAllText(Path.Combine(repo,name),name[..1]+"0\n");
            Commit(repo,"baseline");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"a1\n");
            File.WriteAllText(Path.Combine(repo,"b.txt"),"b1\n");
            File.WriteAllText(Path.Combine(repo,"staged.txt"),"s1\n");
            ReviewFixtureGit.Require(git,repo,"add","staged.txt");
            File.WriteAllText(Path.Combine(repo,"untracked.txt"),"u0\n");
            var service=new ReviewService();
            var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            var commit=service.CommitAsync(snapshot,["a.txt"],"selected reviewed change").GetAwaiter().GetResult();
            Assert(commit.Length==40,"提交返回的身份无效");
            Assert(Show(repo,commit,"a.txt")=="a1"&&Show(repo,commit,"b.txt")=="b0"&&Show(repo,commit,"staged.txt")=="s0","提交包含了未选文件或已 staged 内容");
            Assert(Status(repo).Contains("?? untracked.txt",StringComparison.Ordinal),"未跟踪文件被意外提交或删除");
            Assert(ReviewFixtureGit.Require(git,repo,"diff","--cached","--name-only").Trim()=="staged.txt","既有 staged 内容未被保留");
            var beforeHead=Head(repo);
            var stale=service.CaptureAsync(repo).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(repo,"b.txt"),"b2\n");
            Throws<IOException>(()=>service.CommitAsync(stale,["b.txt"],"must reject").GetAwaiter().GetResult(),"审阅后修改的内容未被拒绝");
            Assert(Head(repo)==beforeHead,"拒绝后 HEAD 被移动");
            var indexSnapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(repo,"staged.txt"),"s2\n");
            ReviewFixtureGit.Require(git,repo,"add","staged.txt");
            Throws<IOException>(()=>service.CommitAsync(indexSnapshot,["b.txt"],"must reject").GetAwaiter().GetResult(),"索引变化未被拒绝");
            Assert(Head(repo)==beforeHead,"索引拒绝后 HEAD 被移动");
            var headSnapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            ReviewFixtureGit.Require(git,repo,"add","b.txt");
            ReviewFixtureGit.Require(git,repo,"commit","-qm","agent moved head");
            Throws<IOException>(()=>service.CommitAsync(headSnapshot,["b.txt"],"must reject").GetAwaiter().GetResult(),"HEAD 变化未被拒绝");
        });

        test("S4 concurrent index writer blocks commit before HEAD advances",()=>
        {
            var repo=NewRepo("index-lock");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"baseline\n");Commit(repo,"baseline");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"reviewed\n");
            var service=new ReviewService();var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            var index=Path.Combine(repo,".git","index");var beforeIndex=File.ReadAllBytes(index);
            using(var competingWriter=new FileStream(index+".lock",FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                Throws<IOException>(()=>service.CommitAsync(snapshot,["a.txt"],"must reject locked index").GetAwaiter().GetResult(),"index lock was ignored");
                Assert(Head(repo)==snapshot.HeadSha&&File.ReadAllBytes(index).SequenceEqual(beforeIndex),"rejected concurrent writer still moved HEAD or overwrote index");
            }
        });

        test("S4 push preserves repository pre-push refusal",()=>
        {
            var repo=NewRepo("push-hook");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"baseline\n");Commit(repo,"baseline");
            var remote=Path.Combine(fixtureRoot,"remote-"+Guid.NewGuid().ToString("N")+".git");
            ReviewFixtureGit.Require(git,fixtureRoot,"init","--bare","-q",remote);
            ReviewFixtureGit.Require(git,repo,"remote","add","origin",remote);
            File.WriteAllText(Path.Combine(repo,".git","hooks","pre-push"),"#!/bin/sh\necho refused-by-project >&2\nexit 1\n",new UTF8Encoding(false));
            var service=new ReviewService();var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            Throws<IOException>(()=>service.PushAsync(snapshot).GetAwaiter().GetResult(),"pre-push refusal was bypassed");
            Assert(ReviewFixtureGit.Require(git,repo,"ls-remote","--heads","origin").Trim().Length==0,"refused push changed remote heads");
        });

        test("S4 commit honors repository pre-commit refusal without altering HEAD or index",()=>
        {
            var repo=NewRepo("commit-hook");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"baseline\n");Commit(repo,"baseline");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"reviewed\n");
            File.WriteAllText(Path.Combine(repo,".git","hooks","pre-commit"),"#!/bin/sh\necho refused-by-project >&2\nexit 1\n",new UTF8Encoding(false));
            var service=new ReviewService();var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            var index=File.ReadAllBytes(Path.Combine(repo,".git","index"));
            Throws<IOException>(()=>service.CommitAsync(snapshot,["a.txt"],"must honor hook").GetAwaiter().GetResult(),"pre-commit refusal was bypassed");
            Assert(Head(repo)==snapshot.HeadSha&&File.ReadAllBytes(Path.Combine(repo,".git","index")).SequenceEqual(index),"refused commit changed HEAD or index");
        });

        test("S4 untracked files are never committed unless explicitly selected",()=>
        {
            var repo=NewRepo("untracked");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"a0\n");Commit(repo,"baseline");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"a1\n");
            File.WriteAllText(Path.Combine(repo,"keep.txt"),"never committed\n");
            var service=new ReviewService();
            var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            var commit=service.CommitAsync(snapshot,["a.txt"],"tracked only").GetAwaiter().GetResult();
            var tracked=ReviewFixtureGit.Run(git,repo,["cat-file","-e",commit+":keep.txt"]);
            Assert(tracked.Code!=0,"未勾选的未跟踪文件被提交");
            Assert(Status(repo).Contains("?? keep.txt",StringComparison.Ordinal),"未跟踪文件状态被改变");
        });

        test("S4 review store is bounded and never overwrites corrupt or future files",()=>
        {
            var path=Path.Combine(root,"review-store-"+Guid.NewGuid().ToString("N")+".json");
            var store=new ReviewStore(path);
            VerificationResult MakeResult(int index)=>new(Guid.NewGuid(),Guid.NewGuid(),new string('a',40),new string('b',64),Guid.NewGuid(),new string('c',64),"Passed",0,index,"out","err",DateTimeOffset.UtcNow.AddSeconds(index),"fixture-workspace");
            for(var index=0;index<105;index++)store.SaveResult(MakeResult(index));
            var book=store.Load();
            Assert(book.Results.Count==100,"结果数量未限制为 100");
            var profile=new VerificationProfile(Guid.NewGuid(),"fixture",new CommandSpec(Path.Combine(Environment.SystemDirectory,"cmd.exe"),["/c","echo"],"exe"),30);
            store.SaveProfile(profile);
            Assert(store.Load().Profiles.Single().Name=="fixture","命名检查未持久化");
            var huge=MakeResult(200)with{Output=new string('x',100000)};
            store.SaveResult(huge);
            var stored=store.Load().Results.Single(result=>result.Id==huge.Id);
            Assert(stored.Output.Length<=70*1024&&stored.Output.Contains("日志已截断"),"结果日志未保持有界");
            var corrupt=Encoding.UTF8.GetBytes("{broken");
            File.WriteAllBytes(path,corrupt);
            Throws<InvalidDataException>(()=>store.Load(),"损坏记录未被拒绝");
            Throws<InvalidDataException>(()=>store.SaveResult(MakeResult(300)),"损坏记录被覆盖");
            Assert(File.ReadAllBytes(path).SequenceEqual(corrupt),"损坏记录原文件被修改");
            Store.Save(path,new ReviewBook{SchemaVersion=2});
            var future=File.ReadAllBytes(path);
            Throws<InvalidDataException>(()=>store.SaveProfile(profile),"未来版本记录未被拒绝");
            Assert(File.ReadAllBytes(path).SequenceEqual(future),"未来版本记录原文件被修改");
            void RejectsRaw(string payload,string label)
            {
                var bytes=Encoding.UTF8.GetBytes(payload);
                File.WriteAllBytes(path,bytes);
                Throws<InvalidDataException>(()=>store.Load(),label+"未被拒绝");
                Throws<InvalidDataException>(()=>store.SaveResult(MakeResult(300)),label+"被 SaveResult 覆盖");
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes),label+"原文件被修改");
            }
            RejectsRaw("{}","缺少全部必需字段的记录");
            RejectsRaw("{\"SchemaVersion\":1,\"Profiles\":[]}","缺少检查结果的记录");
            RejectsRaw("{\"SchemaVersion\":\"1\",\"Profiles\":[],\"Results\":[]}","版本类型错误的记录");
            RejectsRaw("{\"SchemaVersion\":1,\"Profiles\":[null],\"Results\":[]}","空命名检查的记录");
            void RejectsBook(ReviewBook malformed,string label)
            {
                Store.Save(path,malformed);
                var bytes=File.ReadAllBytes(path);
                Throws<InvalidDataException>(()=>store.Load(),label+"未被拒绝");
                Throws<InvalidDataException>(()=>store.SaveResult(MakeResult(301)),label+"被 SaveResult 覆盖");
                Assert(File.ReadAllBytes(path).SequenceEqual(bytes),label+"原文件被修改");
            }
            RejectsBook(new ReviewBook{Profiles=[profile,profile]},"重复命名检查身份的记录");
            var repeatedResult=MakeResult(302);
            RejectsBook(new ReviewBook{Results=[repeatedResult,repeatedResult]},"重复检查结果身份的记录");
            RejectsBook(new ReviewBook{Profiles=Enumerable.Range(0,101).Select(index=>profile with{Id=Guid.NewGuid(),Name="profile-"+index}).ToList()},"命名检查过多的记录");
            RejectsBook(new ReviewBook{Results=Enumerable.Range(0,101).Select(MakeResult).ToList()},"检查结果过多的记录");
            using(var oversized=new FileStream(path,FileMode.Create,FileAccess.Write,FileShare.None))oversized.SetLength(9L*1024*1024);
            Throws<InvalidDataException>(()=>store.Load(),"超出大小上限的记录未被拒绝");
            Throws<InvalidDataException>(()=>store.SaveResult(MakeResult(303)),"超出大小上限的记录被覆盖");
            Assert(new FileInfo(path).Length==9L*1024*1024,"超出大小上限的记录原文件被修改");
        });

        test("S4 CI, push and PR fail closed without a GitHub remote",()=>
        {
            var repo=NewRepo("ci");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"a0\n");Commit(repo,"baseline");
            var service=new ReviewService();
            var snapshot=service.CaptureAsync(repo).GetAwaiter().GetResult();
            var ci=service.GetCiAsync(snapshot).GetAwaiter().GetResult();
            Assert(ci.State=="Unknown"&&ci.Runs.Length==0&&ci.Message.Contains("远程"),"无远程仓库被报告为绿色 CI");
            Throws<IOException>(()=>service.PushAsync(snapshot).GetAwaiter().GetResult(),"无远程仓库仍允许推送");
            Throws<IOException>(()=>service.CreatePullRequestAsync(snapshot,"title","body").GetAwaiter().GetResult(),"无远程仓库仍允许创建 PR");
            Throws<IOException>(()=>service.PreviewPortsAsync(Guid.NewGuid()).GetAwaiter().GetResult(),"不存在的尝试任务组未可读失败");
        });
        test("P3 GitChanges reports branch, baseline, uncommitted, stat and ahead/behind",()=>
        {
            var repo=NewRepo("git-changes");
            File.WriteAllText(Path.Combine(repo,"base.txt"),"base\n");Commit(repo,"base");
            ReviewFixtureGit.Require(git,repo,"checkout","-q","-b","feature/x");
            File.WriteAllText(Path.Combine(repo,"base.txt"),"changed\n");Commit(repo,"first feature");
            File.WriteAllText(Path.Combine(repo,"third.txt"),"third\n");Commit(repo,"second feature");
            File.WriteAllText(Path.Combine(repo,"base.txt"),"dirty\n");
            File.WriteAllText(Path.Combine(repo,"loose.txt"),"loose\n");
            var changes=GitChanges.GetWorktreeChanges(repo,git);
            Assert(changes.Ok,"read-only changes failed: "+changes.Error);
            Assert(changes.Branch=="feature/x","branch mismatch: "+changes.Branch);
            Assert(!changes.HasUpstream,"local-only branch was reported as having an upstream");
            Assert(changes.UncommittedCount==2,"uncommitted count mismatch: "+changes.UncommittedCount);
            Assert(changes.DiffStat.Contains("base.txt"),"diff stat missing changed file: "+changes.DiffStat);
            Assert(changes.Ahead==2,"ahead mismatch: "+changes.Ahead);
            Assert(changes.Behind==0,"behind mismatch: "+changes.Behind);
            Assert(changes.Baseline is "master" or "main","fallback baseline mismatch: "+changes.Baseline);
        });

        test("UX12 changes include untracked-only files and retain launch commit when branch advances",()=>
        {
            var repo=NewRepo("fixed-baseline");File.WriteAllText(Path.Combine(repo,"base.txt"),"base\n");Commit(repo,"base");
            var baseline=GitChanges.ReadHead(repo)!;
            File.WriteAllText(Path.Combine(repo,"新增 中文.txt"),"new\n");
            var first=GitChanges.GetWorktreeChanges(repo,git,baseline);
            Assert(first.Ok&&first.PinnedBaseline&&first.UntrackedFiles.SequenceEqual(new[]{"新增 中文.txt"})&&first.UncommittedCount==1,"untracked-only task disappeared");
            Commit(repo,"advance default branch");
            var moved=GitChanges.GetWorktreeChanges(repo,git,baseline);
            Assert(moved.Ok&&moved.Baseline==baseline&&moved.Ahead==1&&moved.DiffStat.Length>0,"moving branch erased task diff");
            Assert(!GitChanges.GetWorktreeChanges(repo,git,new string('f',40)).Ok,"missing launch commit silently fell back");
            ReviewFixtureGit.Require(git,repo,"mv","base.txt","renamed.txt");
            Assert(GitChanges.GetWorktreeChanges(repo,git,baseline).UncommittedCount==1,"rename counted twice");
        });
        test("P3 GitChanges resolves upstream baseline and counts against a bare remote",()=>
        {
            var bare=Path.Combine(fixtureRoot,"git-changes-remote-"+Guid.NewGuid().ToString("N")+".git");Directory.CreateDirectory(bare);
            ReviewFixtureGit.Require(git,bare,"init","-q","--bare");
            var repo=NewRepo("git-changes-upstream");
            File.WriteAllText(Path.Combine(repo,"a.txt"),"a\n");Commit(repo,"initial");
            ReviewFixtureGit.Require(git,repo,"remote","add","origin",bare);
            var branch=ReviewFixtureGit.Require(git,repo,"rev-parse","--abbrev-ref","HEAD").Trim();
            ReviewFixtureGit.Require(git,repo,"push","-q","-u","origin",branch);
            var changes=GitChanges.GetWorktreeChanges(repo,git);
            Assert(changes.Ok,"upstream changes failed: "+changes.Error);
            Assert(changes.HasUpstream,"upstream not detected");
            Assert(changes.Baseline=="origin/"+branch,"upstream baseline mismatch: "+changes.Baseline);
            Assert(changes.Ahead==0&&changes.Behind==0,"ahead/behind mismatch: "+changes.Ahead+"/"+changes.Behind);
            Assert(changes.UncommittedCount==0,"clean tree reported uncommitted: "+changes.UncommittedCount);
        });

        test("P3 GitChanges refuses missing and non-git directories with structured errors",()=>
        {
            var missing=Path.Combine(fixtureRoot,"git-changes-missing-"+Guid.NewGuid().ToString("N"));
            var absent=GitChanges.GetWorktreeChanges(missing,git);
            Assert(!absent.Ok&&absent.Error.Contains("不存在"),"missing directory not refused: "+absent.Error);
            Assert(absent.Branch.Length==0&&absent.DiffStat.Length==0,"failure leaked default change data");
            var plain=Path.Combine(fixtureRoot,"git-changes-plain-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(plain);
            var nonGit=GitChanges.GetWorktreeChanges(plain,git);
            Assert(!nonGit.Ok&&nonGit.Error.Contains("Git"),"non-git directory not refused: "+nonGit.Error);
        });

        test("P3 GitChanges gh status fails closed when gh is unavailable",()=>
        {
            var original=Environment.GetEnvironmentVariable("PATH");
            var empty=Path.Combine(fixtureRoot,"git-changes-no-gh-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(empty);
            try
            {
                Environment.SetEnvironmentVariable("PATH",empty);
                var missingGh=Path.Combine(empty,"gh.exe");
                var status=GitChanges.GetGhStatus(missingGh);
                Assert(!status.Available&&!status.Authenticated,"missing gh reported as available: "+status.Detail);
                Assert(status.Detail.Contains("gh"),"missing gh detail lacks guidance: "+status.Detail);
            }
            finally{Environment.SetEnvironmentVariable("PATH",original);}
        });

        test("R30 capture returns promptly when a surviving grandchild holds the output pipe",()=>
        {
            var marker="PARENT_"+Guid.NewGuid().ToString("N");
            var watch=Stopwatch.StartNew();
            var capture=ReviewProcess.CaptureAsync("cmd.exe",fixtureRoot,["/d","/c","echo "+marker+" & start /b ping -n 31 127.0.0.1"],CancellationToken.None,8).GetAwaiter().GetResult();
            watch.Stop();
            Assert(!capture.TimedOut,"存活的孙进程不应把捕获判成超时");
            Assert(capture.Code==0,"父进程退出码丢失："+capture.Code);
            Assert(Encoding.UTF8.GetString(capture.Output).Contains(marker),"父进程输出丢失");
            Assert(watch.Elapsed<TimeSpan.FromSeconds(6),"捕获被孙进程持有的管道挂住（"+watch.Elapsed.TotalSeconds.ToString("0.0",CultureInfo.InvariantCulture)+"s）");
        });

        test("R31 ssh toolchain derives Git usr/bin from the resolved git.exe",()=>
        {
            var layout=Path.Combine(fixtureRoot,"gitssh-"+Guid.NewGuid().ToString("N"));
            var cmd=Path.Combine(layout,"cmd");var usrBin=Path.Combine(layout,"usr","bin");
            Directory.CreateDirectory(cmd);Directory.CreateDirectory(usrBin);
            File.WriteAllText(Path.Combine(cmd,"git.exe"),"fixture");
            File.WriteAllText(Path.Combine(usrBin,"ssh-agent.exe"),"fixture");
            var original=Environment.GetEnvironmentVariable("PATH");
            try
            {
                Environment.SetEnvironmentVariable("PATH",cmd);
                var bin=SshAgent.GitSshBinDirectory();
                Assert(bin==usrBin,"未从 git.exe 位置推导 usr/bin："+(bin??"null"));
            }
            finally{Environment.SetEnvironmentVariable("PATH",original);}
        });
    }
}

internal static class ReviewIntegrationChecks
{
    public static async Task RunAsync(Func<string,Func<Task>,Task> test,string root)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("审查集成回归必须在隔离测试根下运行。");
        if(!File.Exists(Store.WorkerPath))throw new FileNotFoundException("缺少 Worker，无法运行审查集成回归。");
        var git=Commands.FindExe("git.exe")??throw new InvalidOperationException("未找到 Git，无法运行审查集成回归。");
        void Assert(bool value,string message){if(!value)throw new Exception(message);}
        string NewRepo()
        {
            var repo=Path.Combine(root,"review-integration-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(repo);
            ReviewFixtureGit.Require(git,repo,"init","-q");
            ReviewFixtureGit.Require(git,repo,"config","user.name","Review Integration");
            ReviewFixtureGit.Require(git,repo,"config","user.email","integration@example.invalid");
            ReviewFixtureGit.Require(git,repo,"config","core.autocrlf","false");
            File.WriteAllText(Path.Combine(repo,"file.txt"),"baseline\n");
            ReviewFixtureGit.Require(git,repo,"add","-A");
            ReviewFixtureGit.Require(git,repo,"commit","-qm","baseline");
            return repo;
        }
        var monitor=await Ipc.EnsureMonitorAsync();
        if(!monitor.Ok)throw new InvalidOperationException("监控器不可用："+monitor.Message);
        var service=new ReviewService();
        await test("S4 verification binds to the reviewed workspace and expires after edits",async()=>
        {
            var repo=NewRepo();
            var marker=Path.Combine(root,"verify-"+Guid.NewGuid().ToString("N")+".json");
            var profile=new VerificationProfile(Guid.NewGuid(),"fixture pass",new CommandSpec(Environment.ProcessPath!,["--echo",marker],"exe"),60);
            var snapshot=await service.CaptureAsync(repo);
            var runId=Guid.NewGuid();
            var result=await service.VerifyAsync(runId,snapshot,profile);
            Assert(result.State=="Passed"&&result.ExitCode==0&&File.Exists(marker),"受管检查未按预期运行："+result.Error);
            Assert(ReviewPolicy.IsCurrent(result,runId,snapshot,ReviewService.CommandFingerprint(profile)),"检查结果未绑定审阅快照");
            File.AppendAllText(Path.Combine(repo,"file.txt"),"changed\n");
            var fresh=await service.CaptureAsync(repo);
            Assert(!ReviewPolicy.IsCurrent(result,runId,fresh,ReviewService.CommandFingerprint(profile)),"工作区修改后旧结果仍为 current");
        });
        await test("S4 a successful command that changes reviewed inputs is stale at completion",async()=>
        {
            var repo=NewRepo();
            var profile=new VerificationProfile(Guid.NewGuid(),"fixture edits input",new CommandSpec(Environment.ProcessPath!,["--echo",Path.Combine(repo,"file.txt")],"exe"),60);
            var snapshot=await service.CaptureAsync(repo);
            var result=await service.VerifyAsync(Guid.NewGuid(),snapshot,profile);
            Assert(result.ExitCode==0&&result.State=="Stale","command changed reviewed input but remained a current pass");
        });

        await test("S4 verification timeout terminates its own descendant processes",async()=>
        {
            var repo=NewRepo();
            var pidFile=Path.Combine(root,"timeout-child-"+Guid.NewGuid().ToString("N")+".pid");
            var profile=new VerificationProfile(Guid.NewGuid(),"fixture timeout",new CommandSpec(Environment.ProcessPath!,["--spawn-child","30000","30000",pidFile],"exe"),3);
            var snapshot=await service.CaptureAsync(repo);
            var result=await service.VerifyAsync(Guid.NewGuid(),snapshot,profile);
            Assert(result.State=="TimedOut"&&result.ExitCode!=0,"检查超时未被标记为 TimedOut");
            var deadline=DateTime.UtcNow.AddSeconds(10);
            while(!File.Exists(pidFile)&&DateTime.UtcNow<deadline)await Task.Delay(50);
            Assert(File.Exists(pidFile),"超时检查未留下子进程证据");
            var pid=int.Parse((await File.ReadAllTextAsync(pidFile)).Trim(),CultureInfo.InvariantCulture);
            var exited=false;deadline=DateTime.UtcNow.AddSeconds(10);
            while(!exited&&DateTime.UtcNow<deadline)
            {
                try{using var process=Process.GetProcessById(pid);exited=process.HasExited;}
                catch(ArgumentException){exited=true;}
                if(!exited)await Task.Delay(100);
            }
            Assert(exited,"超时后子孙进程仍在运行");
        });
        await test("S4 verification cancellation reports Cancelled rather than a pass",async()=>
        {
            var repo=NewRepo();
            var profile=new VerificationProfile(Guid.NewGuid(),"fixture cancel",new CommandSpec(Environment.ProcessPath!,["--hold","30000"],"exe"),60);
            var snapshot=await service.CaptureAsync(repo);
            using var cancellation=new CancellationTokenSource();
            var task=service.VerifyAsync(Guid.NewGuid(),snapshot,profile,cancellation.Token);
            await Task.Delay(2000);
            cancellation.Cancel();
            var result=await task;
            Assert(result.State=="Cancelled"&&result.ExitCode!=0,"取消被报告为通过或仍为运行");
        });
        await test("S4 preview ports list only loopback listeners of the attempt job",async()=>
        {
            var repo=NewRepo();
            var node=Commands.FindExe("node.exe")??throw new InvalidOperationException("未找到 Node.js，无法验证端口发现。");
            var attemptId=Guid.NewGuid();var portFile=Path.Combine(root,"preview-port-"+Guid.NewGuid().ToString("N")+".txt");
            var script="const net=require('node:net'),fs=require('node:fs');const s=net.createServer();s.listen(0,'127.0.0.1',()=>fs.writeFileSync(process.argv[1],String(s.address().port)));setTimeout(()=>s.close(),30000);";
            using var rootJob=new Job(Store.RootJob,openOnly:true);
            using var itemJob=new Job(Store.JobName(attemptId));
            using var process=new ProtectedProcess(new CommandSpec(node,["-e",script,portFile],"exe"),repo,[rootJob,itemJob],redirected:true);
            process.Resume();
            try
            {
                var deadline=DateTime.UtcNow.AddSeconds(15);
                while(!File.Exists(portFile)&&DateTime.UtcNow<deadline)await Task.Delay(100);
                Assert(File.Exists(portFile),"受管监听进程未启动："+(process.Process.HasExited?$"exit={process.Process.ExitCode}":"still running"));
                var port=int.Parse((await File.ReadAllTextAsync(portFile)).Trim(),CultureInfo.InvariantCulture);
                var ports=await service.PreviewPortsAsync(attemptId);
                Assert(ports.Any(candidate=>candidate.Port==port&&candidate.Url=="http://127.0.0.1:"+port.ToString(CultureInfo.InvariantCulture)+"/"),"未列出受管 loopback 端口");
            }
            finally
            {
                Native.TerminateJobObject(itemJob.Handle,1);
                process.Process.WaitForExit(5000);
            }
        });
    }
}

internal sealed record ReviewFixtureResult(int Code,string Output,string Error);

internal static class ReviewFixtureGit
{
    public static ReviewFixtureResult Run(string git,string cwd,IReadOnlyList<string> arguments)
    {
        var start=new ProcessStartInfo(git){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=cwd,StandardOutputEncoding=new UTF8Encoding(false),StandardErrorEncoding=new UTF8Encoding(false)};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);
        start.Environment["GIT_TERMINAL_PROMPT"]="0";
        using var process=Process.Start(start)??throw new InvalidOperationException("无法启动 Git。");
        var output=process.StandardOutput.ReadToEndAsync();var error=process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return new(process.ExitCode,output.GetAwaiter().GetResult(),error.GetAwaiter().GetResult());
    }
    public static string Require(string git,string cwd,params string[] arguments)
    {
        var result=Run(git,cwd,arguments);
        if(result.Code!=0)throw new InvalidOperationException("git "+string.Join(" ",arguments)+" 失败："+result.Error.Trim());
        return result.Output;
    }
}
