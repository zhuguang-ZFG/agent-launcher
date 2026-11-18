#if INTEGRATION_TESTS
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;

public partial class MainWindow
{
    private async Task CheckEmbeddedAsync(string report)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-"))throw new InvalidOperationException("Embedded checks require an isolated test root.");
        var project=Directory.CreateDirectory(Path.Combine(Store.Root,"project")).FullName;
        var node=Commands.FindExe("node.exe")??throw new IOException("Node fixture missing.");
        const string fixture="process.stdin.setEncoding('utf8');console.log('READY');process.stdin.on('data',s=>{console.log('ACK '+s.trim());if(s.includes('quit'))process.exit(0)});setTimeout(()=>process.exit(2),60000)";
        var slot=new Slot{Enabled=true,AgentId="custom",CustomName="本机 Agent",Profiles=new(){["custom"]=new(){Executable=node,Arguments=["-e",fixture]}}};
        config=new Configuration{Draft=new(){Project=project,Slots=[slot]}};
        Store.Save(configPath,config);Render();
        await LaunchAsync();
        var id=embeddedViews.Keys.Single();
        string ScreenText()
        {
            if(embeddedViews[id].Card.Applied is not {} snapshot)return "";
            var text=new StringBuilder();
            foreach(var cell in snapshot.Visible)if(!cell.IsContinuation)text.Append(cell.Codepoint==0?" ":char.ConvertFromUtf32(cell.Codepoint));
            return text.ToString();
        }
        async Task WaitFor(Func<bool> ready,string message)
        {
            for(var i=0;i<100;i++){if(ready())return;await Task.Delay(100);}
            throw new IOException(message+" / "+embeddedViews[id].Status.Text+" / frames="+embeddedViews[id].Card.View.AppliedCount+" / "+ScreenText().Trim());
        }
        await WaitFor(()=>ScreenText().Contains("READY"),"WPF未收到首屏");
        var plan=status!.Entries!.Single(e=>e.Plan.Id==id).Plan;
        RemoveEmbedded(id);
        if(!await TryShowEmbeddedAsync(plan))throw new IOException("不能从记录重新打开");
        await WaitFor(()=>ScreenText().Contains("READY"),"重新打开未恢复首屏");
        var view=embeddedViews[id];
        if(!view.Card.Paste("中文 Agent"))throw new IOException("输入失败");
        await view.Client!.SendInputAsync(new byte[]{13},default);
        await WaitFor(()=>ScreenText().Contains("ACK 中文 Agent"),"子进程未收到输入");
        UpdateLayout();
        var dpi=VisualTreeHelper.GetDpi(this);
        var bitmap=new RenderTargetBitmap((int)(ActualWidth*dpi.DpiScaleX),(int)(ActualHeight*dpi.DpiScaleY),dpi.PixelsPerInchX,dpi.PixelsPerInchY,PixelFormats.Pbgra32);
        bitmap.Render(this);var png=new PngBitmapEncoder();png.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=File.Create(report+".png"))png.Save(output);
        view.Card.Paste("quit");await view.Client.SendInputAsync(new byte[]{13},default);
        await WaitFor(()=>Store.Read<Receipt>(Store.ReceiptPath(id)) is {Phase:"Finished",ExitCode:0},"子进程未正常结束");
        Store.Save(report,new{Passed=true,Attempt=id,RestoredScreen=true,Input="ACK 中文 Agent",Exit=0});
    }
    private sealed record StageState(string Stage,Guid Attempt,int AgentPid);
    // design.md §8 生命周期门：返回 "exit"（干净退出）、"close-window"（走真实 WPF 关闭路径）或 "keep-alive"（保持运行等待外部打击）。
    private async Task<string> RunEmbeddedStageAsync(string stage,string statePath)
    {
        if(!Store.Root.Contains("AgentLauncher-tests-"))throw new InvalidOperationException("Lifecycle stages require an isolated test root.");
        if(stage=="reopen")return await RunReopenStageAsync(statePath);
        var project=Directory.CreateDirectory(Path.Combine(Store.Root,"project")).FullName;
        var node=Commands.FindExe("node.exe")??throw new IOException("Node fixture missing.");
        var markPath=Path.Combine(Store.Root,"drain-marker.txt").Replace('\\','/');
        var recordPath=Path.Combine(Store.Root,"interrupt-record.txt").Replace('\\','/');
        var fixture=stage switch
        {
            "interrupt"=>"const fs=require('fs');if(process.stdin.setRawMode)process.stdin.setRawMode(true);process.stdin.setEncoding('latin1');console.log('READY');process.stdin.on('data',s=>{fs.appendFileSync('"+recordPath+"',Buffer.from(s,'latin1'));if(s.includes('\\x03'))process.exit(0)});setTimeout(()=>process.exit(9),120000)",
            "drain"=>"const fs=require('fs');console.log('READY');process.stdin.setEncoding('utf8');process.stdin.on('data',s=>{if(s.includes('go')){const line='X'.repeat(127)+'\\n';let left=65536;function step(){while(left>0){if(!process.stdout.write(line)){left--;process.stdout.once('drain',step);return}left--}process.stdout.write('DRAINDONE\\n',()=>fs.writeFileSync('"+markPath+"','done'))}step()}else if(s.includes('quit'))process.exit(0)});setTimeout(()=>process.exit(9),300000)",
            "close-window" or "crash-wait" or "worker-exit"=>"console.log('READY');process.stdin.setEncoding('utf8');process.stdin.on('data',s=>{if(s.includes('quit'))process.exit(0)});setTimeout(()=>process.exit(9),600000)",
            _=>throw new IOException("未知 lifecycle 阶段: "+stage)
        };
        var slot=new Slot{Enabled=true,AgentId="custom",CustomName="本机 Agent",Profiles=new(){["custom"]=new(){Executable=node,Arguments=["-e",fixture]}}};
        config=new Configuration{Draft=new(){Project=project,Slots=[slot]}};
        Store.Save(configPath,config);Render();
        await LaunchAsync();
        var id=embeddedViews.Keys.Single();
        string ScreenText()
        {
            if(!embeddedViews.TryGetValue(id,out var view)||view.Card.Applied is not {} snapshot)return "";
            var text=new StringBuilder();
            foreach(var cell in snapshot.Visible)if(!cell.IsContinuation)text.Append(cell.Codepoint==0?" ":char.ConvertFromUtf32(cell.Codepoint));
            return text.ToString();
        }
        async Task WaitFor(Func<bool> ready,string message)
        {
            for(var i=0;i<300;i++){if(ready())return;await Task.Delay(100);}
            var info=embeddedViews.TryGetValue(id,out var current)?current:null;
            throw new IOException(message+" / "+(info?.Status.Text??"视图已分离")+" / frames="+(info is null?-1:info.Card.View.AppliedCount)+" / "+ScreenText().Trim());
        }
        await WaitFor(()=>ScreenText().Contains("READY"),stage+" 未收到首屏");
        var receipt=Store.Read<Receipt>(Store.ReceiptPath(id))??throw new IOException(stage+" 缺少回执");
        if(receipt.Phase!="Started")throw new IOException(stage+" 回执不是 Started: "+receipt.Phase);
        Store.Save(statePath,new{Stage=stage,Attempt=id,AgentPid=receipt.Pid,StartedAt=DateTimeOffset.UtcNow});
        switch(stage)
        {
            case "interrupt":
            {
                await embeddedViews[id].Client!.SendInputAsync(TerminalInput.Interrupt(),default);
                await WaitFor(()=>Store.Read<Receipt>(Store.ReceiptPath(id)) is {Phase:"Finished",ExitCode:0},"中断后子进程未以 0 自然退出");
                var record=File.Exists(recordPath)?File.ReadAllText(recordPath):"";
                if(!record.Contains('\x3'))throw new IOException("0x03 未进入子进程 stdin 记录（"+record.Length+" 字节）");
                RemoveEmbedded(id);
                Store.Save(statePath,new{Stage=stage,Attempt=id,AgentPid=receipt.Pid,Passed=true,InterruptByteReceived=true,RecordBytes=record.Length,Receipt="Finished",ExitCode=0});
                return "exit";
            }
            case "drain":
            {
                var before=embeddedViews[id].Card.Applied?.Generation??0;
                await embeddedViews[id].Client!.SendInputAsync(Encoding.UTF8.GetBytes("go\r"),default);
                await Task.Delay(300);
                var detachedAt=DateTimeOffset.UtcNow;
                RemoveEmbedded(id);
                await WaitFor(()=>File.Exists(markPath),"8 MiB 未在期限内写完");
                var writeMs=(long)(DateTimeOffset.UtcNow-detachedAt).TotalMilliseconds;
                if(writeMs>30000)throw new IOException("8 MiB 写入超过 30 秒: "+writeMs);
                if(!await TryShowEmbeddedAsync(status!.Entries!.Single(e=>e.Plan.Id==id).Plan))throw new IOException("丢弃后不能重新连接既有 host");
                await WaitFor(()=>ScreenText().Contains("DRAINDONE"),"重连后未恢复到最新画面");
                var view=embeddedViews[id];
                var after=view.Card.Applied?.Generation??0;
                var frames=view.Card.View.AppliedCount;
                if(after<=before)throw new IOException($"重连未应用新的完整代: {before} -> {after}");
                if(frames>10)throw new IOException("重连后帧数异常: "+frames);
                view.Card.Paste("quit");await view.Client!.SendInputAsync(new byte[]{13},default);
                await WaitFor(()=>Store.Read<Receipt>(Store.ReceiptPath(id)) is {Phase:"Finished",ExitCode:0},"drain 后子进程未正常结束");
                RemoveEmbedded(id);
                Store.Save(statePath,new{Stage=stage,Attempt=id,AgentPid=receipt.Pid,Passed=true,WriteMs=writeMs,GenerationBefore=before,GenerationAfter=after,FramesAppliedAfterReconnect=frames,Receipt="Finished",ExitCode=0});
                return "exit";
            }
            case "close-window":
                Close();
                return "close-window";
            default: // crash-wait / worker-exit：保持 UI 运行，由外部脚本完成打击动作。
                return "keep-alive";
        }
    }
    private async Task<string> RunReopenStageAsync(string statePath)
    {
        var previous=Store.Read<StageState>(statePath)??throw new IOException("缺少 prepare 阶段状态文件");
        LaunchItem? plan=null;
        for(var i=0;i<200;i++)
        {
            await RefreshAsync();
            plan=status?.Entries?.FirstOrDefault(e=>e.Plan.Id==previous.Attempt&&!Store.IsTerminal(e))?.Plan;
            if(plan!=null)break;
            await Task.Delay(100);
        }
        if(plan is null)throw new IOException("重新打开后账本中没有该尝试的非终态条目");
        var receipt=Store.Read<Receipt>(Store.ReceiptPath(previous.Attempt));
        if(receipt?.Pid!=previous.AgentPid)throw new IOException("回执缺失或 Agent pid 已变化: "+receipt?.Pid);
        if(!await TryShowEmbeddedAsync(plan))throw new IOException("未能从既有 host 恢复终端画面");
        var id=previous.Attempt;
        string ScreenText()
        {
            if(embeddedViews[id].Card.Applied is not {} snapshot)return "";
            var text=new StringBuilder();
            foreach(var cell in snapshot.Visible)if(!cell.IsContinuation)text.Append(cell.Codepoint==0?" ":char.ConvertFromUtf32(cell.Codepoint));
            return text.ToString();
        }
        var restored=false;
        for(var i=0;i<300;i++){restored=ScreenText().Contains("READY");if(restored)break;await Task.Delay(100);}
        if(!restored)throw new IOException("恢复后画面没有 READY / "+embeddedViews[id].Status.Text);
        var view=embeddedViews[id];
        view.Card.Paste("quit");await view.Client!.SendInputAsync(new byte[]{13},default);
        for(var i=0;i<300;i++){if(Store.Read<Receipt>(Store.ReceiptPath(id)) is {Phase:"Finished",ExitCode:0})break;await Task.Delay(100);}
        if(Store.Read<Receipt>(Store.ReceiptPath(id)) is not {Phase:"Finished",ExitCode:0})throw new IOException("恢复后的会话未正常结束");
        RemoveEmbedded(id);
        Store.Save(statePath,new{Stage="reopen",Attempt=id,AgentPid=previous.AgentPid,Passed=true,LedgerShown=true,RestoredScreen=true,SameAgent=true,Receipt="Finished",ExitCode=0});
        return "exit";
    }
}
#endif
