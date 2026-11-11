using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

if(args is ["--integration"]) return await Integration.RunAsync();
if(args is ["--admission-integration"]) return await AdmissionIntegration.RunAsync();
if(args.Length>0&&args[0]=="--echo") { File.WriteAllText(args[1],JsonSerializer.Serialize(new { Cwd=Environment.CurrentDirectory, Args=args.Skip(2).ToArray() }));return 0; }
if(args.Length>0&&args[0]=="--hold") {Thread.Sleep(int.Parse(args[1]));return 0;}
if(args.Length>2&&args[0]=="--spawn-late") {var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};psi.ArgumentList.Add("--write-after");psi.ArgumentList.Add(args[1]);psi.ArgumentList.Add(args[2]);_ = Process.Start(psi);return 0;}
if(args.Length>2&&args[0]=="--write-after"){Thread.Sleep(int.Parse(args[1]));File.WriteAllText(args[2],"late");return 0;}
if(args.Length>0&&args[0]=="--spam"){for(var i=0;i<2000;i++)Console.WriteLine("0123456789012345678901234567890123456789012345678901234567890123");return 0;}
if(args.Length>4&&args[0]=="--nested-setup") {
    var pool=new Job(args[1]);
    AgentLauncher.Windows.Native.Check(AgentLauncher.Windows.Native.AssignProcessToJobObject(pool.Handle,Process.GetCurrentProcess().Handle));
    for(var i=0;i<int.Parse(args[3]);i++)_ = new Job(args[2]+i);
    File.WriteAllText(args[4],"ready");
    if(args.Length>5&&args[5]=="exit")return 0;
    Thread.Sleep(30000);return 0;
}
if(args.Length>3&&args[0]=="--spawn-child") {
    var psi=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false};
    psi.ArgumentList.Add("--hold");psi.ArgumentList.Add(args[2]);
    using var child=Process.Start(psi)!;
    File.WriteAllText(args[3],child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    Thread.Sleep(int.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture));return 0;
}
if(args.Length>1&&args[0]=="--allocate-mib") {
    var hold=args.Length>2?int.Parse(args[2]):0;
    try {var data=new byte[int.Parse(args[1])*1024*1024];for(var i=0;i<data.Length;i+=4096)data[i]=1;if(args.Length>3)File.WriteAllText(args[3],"ok");if(hold>0)Thread.Sleep(hold);return 10;}
    catch(OutOfMemoryException){return 0;}
}
if(args.Length>0&&args[0]=="--allocate") {try {var data=new byte[256*1024*1024];for(var i=0;i<data.Length;i+=4096)data[i]=1;return 10;}catch(OutOfMemoryException){return 0;}}
var failures=new List<string>();var passed=new List<string>();var temp=Path.Combine(Path.GetTempPath(),"AgentLauncher-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
void Test(string name,Action action){try{action();passed.Add(name);Console.WriteLine("PASS "+name);}catch(Exception e){failures.Add(name+": "+e);Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Assert(bool value,string message){if(!value)throw new Exception(message);}
Test("P01 rapid native sampling retains a valid CPU interval",()=>{
    var sampler=new Sampler();var first=sampler.Sample();
    Assert(double.IsNaN(first.CpuPercent),"first CPU reading fabricated usage");
    Thread.Sleep(250);var previous=sampler.Sample();
    Assert(double.IsFinite(previous.CpuPercent),"CPU warmup failed");
    var reused=0;
    for(var i=0;i<1000;i++)
    {
        var sample=sampler.Sample();
        Assert(double.IsFinite(sample.CpuPercent),"rapid sample lost its valid CPU interval");
        Assert(sample.At>=previous.At,"sample time moved backwards");
        if(sample.At==previous.At){reused++;Assert(sample.CpuPercent==previous.CpuPercent,"cached interval changed CPU usage");}
        JsonSerializer.Serialize(new Response(true,"sample",Memory:sample),Store.Json);
        previous=sample;
    }
    Assert(reused>0,"native burst did not exercise an unchanged CPU tick");
});
Test("P01 regressing CPU counters preserve the baseline and never renew stale evidence",()=>{
    var sampler=new Sampler();sampler.Sample();Thread.Sleep(250);var valid=sampler.Sample();
    Assert(double.IsFinite(valid.CpuPercent),"CPU warmup failed");
    const System.Reflection.BindingFlags flags=System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic;
    var kernel=typeof(Sampler).GetField("kernel",flags)!;var previous=typeof(Sampler).GetField("previous",flags)!;
    // Put the baseline ahead of the live counter to deterministically exercise a backwards interval.
    const long futureCounter=long.MaxValue/4;kernel.SetValue(sampler,futureCounter);
    var cached=sampler.Sample();
    Assert(cached.CpuPercent==valid.CpuPercent&&cached.At==valid.At,"counter regression discarded or renewed CPU evidence");
    Assert((long)kernel.GetValue(sampler)! == futureCounter,"regressing counter replaced the valid baseline");
    previous.SetValue(sampler,valid with{At=DateTimeOffset.UtcNow.AddSeconds(-ResourcePolicy.SampleTtlSeconds-1)});
    var stale=sampler.Sample();
    Assert(!ResourcePolicy.IsSampleValid(stale),"repeated counter regression renewed stale evidence");
});
Test("P01 unavailable CPU sends a readable fail-closed resource response",()=>{
    foreach(var cpu in new[]{double.NaN,double.PositiveInfinity,double.NegativeInfinity})
    {
        var memory=new MemorySnapshot(16*ResourcePolicy.GiB,12*ResourcePolicy.GiB,32*ResourcePolicy.GiB,24*ResourcePolicy.GiB,cpu,DateTimeOffset.UtcNow);
        using var stream=new MemoryStream();
        Ipc.WriteResponseAsync(stream,new(false,"资源读数不可用",Memory:memory,Decision:nameof(AdmissionKind.Deny)),default).GetAwaiter().GetResult();
        stream.Position=0;var reply=Ipc.ReadResponseAsync(stream,default).GetAwaiter().GetResult();
        Assert(!reply.Ok&&reply.Memory==null&&reply.Decision==nameof(AdmissionKind.Deny),"unknown sample became a launch authorization");
    }
});
Test("F15 profile switching",()=>{var c=new Configuration();c.Edit(d=>d.Slots[0].Profiles["pi"]=new(){Executable="pi.exe",Arguments=["--pi"]});c.Edit(d=>d.Slots[0].AgentId="codex");Assert(c.Draft.Slots[0].Current.Executable=="","stale executable");c.Edit(d=>d.Slots[0].AgentId="pi");Assert(c.Draft.Slots[0].Current.Arguments[0]=="--pi","lost profile");});
Test("F16/F17 full snapshots and LastEdited",()=>{var c=new Configuration();c.Edit(d=>{d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.DirectoryMode="prepared";});c.SaveTemplate("a");c.Restore(new());c.Restore(new());c.Restore(c.LastEdited!);Assert(c.Draft.Slots[3].Enabled&&!c.Draft.Slots[0].Enabled&&c.Draft.DirectoryMode=="prepared","snapshot lost");c.Edit(d=>d.DirectoryMode="shared");Assert(c.Templates["a"].DirectoryMode=="prepared","snapshot aliases draft");});
Test("P low memory rejects without spawning",()=>{var total=16*ResourcePolicy.GiB;var policy=new ResourcePolicySnapshot("practical",ResourcePolicy.DefaultJobMemoryLimit,ResourcePolicy.DefaultPlanningBytes,ResourcePolicy.DefaultPhysicalFloor(total),ResourcePolicy.DefaultCommitFloor(32*ResourcePolicy.GiB),ResourcePolicy.DefaultCpuRate,ResourcePolicy.GitJobMemoryLimit,ResourcePolicy.DefaultGitPlanningBytes);var d=ResourcePolicy.Evaluate(new MemorySnapshot(total,ResourcePolicy.GiB,32*ResourcePolicy.GiB,20*ResourcePolicy.GiB,10,DateTimeOffset.UtcNow),policy,[],[new BudgetItem(Guid.NewGuid(),"agent",ResourcePolicy.DefaultPlanningBytes,ResourcePolicy.DefaultJobMemoryLimit)]);Assert(d.Kind==AdmissionKind.Deny,"low memory allowed");});
Test("F atomic persistence and backup",()=>{var p=Path.Combine(temp,"config.json");Store.Save(p,new Configuration());var c=new Configuration();c.Draft.Project=temp;Store.Save(p,c);Assert(Store.Read<Configuration>(p)!.Draft.Project==temp&&File.Exists(p+".bak"),"save/backup failed");});
Test("R11 required schema fields refuse partial configuration",()=>{bool rejected=false;try{JsonSerializer.Deserialize<Configuration>("{}",Store.Json);}catch(JsonException){rejected=true;}Assert(rejected,"empty config accepted");});
Test("R12 receipt atomic replacement concurrent reader",()=>{
 var file=Path.Combine(temp,"race.json");Store.Save(file,new Receipt("Started",123,456,null,"old",DateTimeOffset.UtcNow));
 var writer=Task.Run(()=>{for(var i=0;i<80;i++)Store.Save(file,new Receipt("Started",123,456,i,"new",DateTimeOffset.UtcNow));});
 for(var i=0;i<200;i++){var receipt=Store.Read<Receipt>(file);Assert(receipt?.Pid==123,"partial receipt observed");}writer.GetAwaiter().GetResult();Assert(Store.Read<Receipt>(file)?.ExitCode==79,"latest receipt lost");
});
Test("F14 physical identity and containment",()=>{var child=Path.Combine(temp,"子目录");Directory.CreateDirectory(child);Assert(PhysicalDirectory.Resolve(temp).Key==PhysicalDirectory.Resolve(temp.ToUpperInvariant()).Key,"case alias differs");Assert(PhysicalDirectory.Overlaps(PhysicalDirectory.Workspace(child),PhysicalDirectory.Workspace(temp)),"containment missed");});
Test("G1 native Job creation + exact argv/cwd",()=>{
 var cwd=Path.Combine(temp,"中文 space & ; 文件夹");Directory.CreateDirectory(cwd);var output=Path.Combine(temp,"args.json");
 using var root=new Job(@"Local\AL-test-root-"+Guid.NewGuid(),root:true);using var job=new Job(@"Local\AL-test-child-"+Guid.NewGuid(),128*1024*1024);
 var expected=new[]{"a b","中文","a;split-pane","\"quoted\"","tail\\","&echo",""};
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--echo",output,..expected],"exe"),cwd,root,job);
 Assert(job.Members().Contains(p.Process.Id),"not in Job at creation");Assert(!File.Exists(output),"ran before Resume");p.Resume();Assert(p.Process.WaitForExit(5000),"child timeout");
 using var data=JsonDocument.Parse(File.ReadAllText(output));Assert(data.RootElement.GetProperty("Args").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(expected),"argv corrupt");Assert(data.RootElement.GetProperty("Cwd").GetString()==cwd,"cwd corrupt");
});
Test("G1 Job hard memory cap causes allocation refusal",()=>{
 using var root=new Job(@"Local\AL-memory-root-"+Guid.NewGuid(),root:true);using var job=new Job(@"Local\AL-memory-child-"+Guid.NewGuid(),96*1024*1024);
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--allocate"],"exe"),temp,root,job);p.Resume();Assert(p.Process.WaitForExit(6000),"child timeout");Assert(p.Process.ExitCode==0,"allocation not refused cleanly");
});
Test("P02 Job memory evidence survives child exit and never invents crash cause",()=>{
 using var root=new Job(@"Local\AL-evidence-root-"+Guid.NewGuid(),root:true);using var job=new Job(@"Local\AL-evidence-child-"+Guid.NewGuid(),96*ResourcePolicy.MiB);
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--allocate"],"exe"),temp,root,job);p.Resume();Assert(p.Process.WaitForExit(6000),"child timeout");
 var evidence=job.QueryMemory();Assert(evidence is {PeakBytes:>0}&&evidence.LimitBytes==96*ResourcePolicy.MiB,"missing kernel peak or limit");
 job.Handle.Dispose();Assert(job.QueryMemory()==null,"unavailable handle fabricated evidence");
 Assert(ExitDiagnostics.Describe(-1073740791,null).Contains("0xC0000409")&&!ExitDiagnostics.Describe(-1073740791,null).Contains("已接近"),"exit code invented memory evidence");
 Assert(!ExitDiagnostics.NearLimit(new(100,89))&&ExitDiagnostics.NearLimit(new(100,90))&&!ExitDiagnostics.NearLimit(new(0,100)),"pressure threshold incorrect");
 var receipt=new Receipt("Finished",1,2,-1073740791,"test",DateTimeOffset.UtcNow,evidence);
 Assert(JsonSerializer.Deserialize<Receipt>(JsonSerializer.Serialize(receipt,Store.Json),Store.Json)?.JobMemory==evidence,"receipt lost evidence");
 Assert(JsonSerializer.Deserialize<Receipt>("{\"Phase\":\"Finished\",\"Pid\":1,\"ProcessTime\":2,\"ExitCode\":0,\"Detail\":\"old\",\"At\":\"2026-11-11T00:00:00Z\"}",Store.Json)?.JobMemory==null,"legacy receipt compatibility lost");
});
Test("ACC-A01 preset argv keeps every argument exact and isolated per agent",()=>{
 var omp=AgentPresets.For("omp",AgentPresets.OmpVerifiedVersion);
 Assert(omp.Count==5&&AgentPresets.For("omp","19.0.0").Count==0,"unknown version exposed presets");
 Assert(AgentPresets.All.All(AgentPresets.StructurallyValid)&&AgentPresets.All.Select(p=>p.Id).Distinct().Count()==AgentPresets.All.Count,"preset table invalid or ids collide");
 Assert(AgentPresets.For("claude",AgentPresets.CodexVerifiedVersion).Count==0&&AgentPresets.For("codex",AgentPresets.CodexVerifiedVersion).All(p=>p.AgentId=="codex"),"preset leaked across agents");
 var resume=omp.Single(p=>p.Id=="omp-resume");
 var filled=resume.Fill("会话 id; --x");
 Assert(filled.Arguments.SequenceEqual(new[]{"--resume","会话 id; --x"})&&resume.Arguments.Contains(ArgumentPreset.ValueToken),"preset fill corrupted arguments or aliased the table");
 Assert(AgentPresets.All.All(p=>!p.Preview.Contains(ArgumentPreset.ValueToken))&&resume.Preview.Contains("会话 ID 前缀或路径"),"preset preview leaked the fill token or lost its value label");
 var rejected=false;try{resume.Fill("  ");}catch(InvalidDataException){rejected=true;}
 Assert(rejected,"empty preset value accepted");
 var plain=omp.Single(p=>p.Id=="omp-continue");Assert(plain.Fill("ignored").Arguments.SequenceEqual(new[]{"--continue"}),"preset without value changed arguments");
 var cwd=Path.Combine(temp,"argv space 中文");Directory.CreateDirectory(cwd);var output=Path.Combine(temp,"preset-args.json");
 var expected=new[]{"--resume","会话 id; --x","--","-x","--name=value","a b","中文","第一行\n第二行","tail\\","","a;split-pane"};
 using var root=new Job(@"Local\AL-preset-root-"+Guid.NewGuid(),root:true);using var job=new Job(@"Local\AL-preset-child-"+Guid.NewGuid(),128*1024*1024);
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--echo",output,..expected],"exe"),cwd,root,job);
 p.Resume();Assert(p.Process.WaitForExit(5000),"child timeout");
 using var data=JsonDocument.Parse(File.ReadAllText(output));Assert(data.RootElement.GetProperty("Args").EnumerateArray().Select(x=>x.GetString()).SequenceEqual(expected),"preset argv corrupt");
});
Test("ACC-A02 version parsing and bounded probe leave no descendant",()=>{
 Assert(AgentVersion.Parse("omp/18.18.8\n")=="18.18.8"&&AgentVersion.Parse("codex-cli 0.167.1")=="0.167.1"&&AgentVersion.Parse("v1.2.3")=="1.2.3","real version output not parsed");
 Assert(AgentVersion.Parse(null)==null&&AgentVersion.Parse("")==null&&AgentVersion.Parse("Codex CLI")==null&&AgentVersion.Parse(new string('9',64))==null,"noise became a version");
 var git=Commands.FindExe("git.exe");
 if(git!=null)
 {
  var result=AgentProbe.RunAsync(new(git,["--version"],"exe"),temp,TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
  Assert(result is{TimedOut:false,ExitCode:0}&&result.Output.Contains("git version"),"real version probe failed: "+result.Error);
  var fingerprint=AgentProbe.Fingerprint(git);Assert(fingerprint.Length==64&&fingerprint==AgentProbe.Fingerprint(git)&&fingerprint!=AgentProbe.Fingerprint(Environment.ProcessPath!),"fingerprint unstable or not content based");
 }
 var cmd=Environment.ProcessPath!;
 var marker=Path.Combine(temp,"probe-descendant.txt");
 if(File.Exists(marker))File.Delete(marker);
 var timed=AgentProbe.RunAsync(new(cmd,["--hold","3000"],"exe"),temp,TimeSpan.FromMilliseconds(500)).GetAwaiter().GetResult();
 Assert(timed.TimedOut&&timed.ExitCode!=0,$"probe did not report a timeout: timedOut={timed.TimedOut} exit={timed.ExitCode} out={timed.Output} err={timed.Error}");
 var spawned=AgentProbe.RunAsync(new(cmd,["--spawn-late","2500",marker],"exe"),temp,TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
 Assert(spawned is{TimedOut:false,ExitCode:0},"descendant fixture did not start");
 Thread.Sleep(3500);
 Assert(!File.Exists(marker),"probe left a descendant writing after the main process exited");
 var capped=AgentProbe.RunAsync(new(cmd,["--spam"],"exe"),temp,TimeSpan.FromSeconds(5),1024).GetAwaiter().GetResult();
 Assert(capped.Truncated&&capped.Output.Length<=1024,$"probe output was not bounded: truncated={capped.Truncated} timedOut={capped.TimedOut} exit={capped.ExitCode} len={capped.Output.Length} errlen={capped.Error.Length}");
});
Test("G1 Job survives monitor handle close while runner retains ownership",()=>{
 var name=@"Local\AL-survive-"+Guid.NewGuid();using var root=new Job(@"Local\AL-survive-root-"+Guid.NewGuid(),root:true);var job=new Job(name,128*1024*1024);using var runnerHandle=new Job(name,openOnly:true);
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","1200"],"exe"),temp,root,job);p.Resume();job.Dispose();using var reopened=new Job(name,openOnly:true);Assert(reopened.Members().Contains(p.Process.Id),"Job lost after monitor close");Assert(p.Process.WaitForExit(4000),"child timeout");
});
Test("G1 CPU cap configured at aggregate root",()=>{using var root=new Job(@"Local\AL-cap-"+Guid.NewGuid(),root:true);var size=Marshal.SizeOf<Native.CpuRate>();var buffer=Marshal.AllocHGlobal(size);try{Native.Check(Native.QueryInformationJobObject(root.Handle,15,buffer,(uint)size,IntPtr.Zero));var rate=Marshal.PtrToStructure<Native.CpuRate>(buffer);Assert(rate.Rate==3500&&(rate.Flags&4)!=0,"CPU cap not configured");}finally{Marshal.FreeHGlobal(buffer);}});
Test("ACC-R05 reopening an existing root Job does not change CPU limits",()=>{
 var name=@"Local\AL-reuse-"+Guid.NewGuid();
 using var first=new Job(name,root:true,cpuRate:ResourcePolicy.DefaultCpuRate);
 using var second=new Job(name,root:true,cpuRate:4000);
 var size=Marshal.SizeOf<Native.CpuRate>();var buffer=Marshal.AllocHGlobal(size);
 try
 {
  Native.Check(Native.QueryInformationJobObject(second.Handle,15,buffer,(uint)size,IntPtr.Zero));
  var rate=Marshal.PtrToStructure<Native.CpuRate>(buffer);
  Assert(rate.Rate==3500&&(rate.Flags&4)!=0,"reopened root Job CPU was changed to "+rate.Rate);
  Assert(second.CreatedNew==false&&first.CreatedNew,"existing root Job was not detected");
 }
 finally{Marshal.FreeHGlobal(buffer);}
});
// F9 regression: each shim template's own node-selection semantics. Template text mirrors the private consts in Commands.Resolve; drift here means the adapter contract changed.
const string piShimTemplate="""
@ECHO off
SETLOCAL
SET "node=node"
"%node%" "%~dp0pi-launcher.js" %*
SET "pi_exit_code=%ERRORLEVEL%"
IF "%pi_exit_code%"=="9009" (
  IF "%node%"=="node" (
    "%SystemRoot%\System32\where.exe" /Q node
    IF ERRORLEVEL 1 GOTO node_missing
  ) ELSE IF NOT EXIST "%node%" GOTO node_missing
)
EXIT /B %pi_exit_code%
:node_missing
ECHO Pi could not find Node.js. Install Node.js 22.19.0 or newer, or rerun the installer at https://pi.dev. 1>&2
EXIT /B %pi_exit_code%
""";
const string npmShimTemplate="""
@ECHO off
GOTO start
:find_dp0
SET dp0=%~dp0
EXIT /b
:start
SETLOCAL
CALL :find_dp0

IF EXIST "%dp0%\node.exe" (
  SET "_prog=%dp0%\node.exe"
) ELSE (
  SET "_prog=node"
  SET PATHEXT=%PATHEXT:;.JS;=;%
)

endLocal & goto #_undefined_# 2>NUL || title %COMSPEC% & "%_prog%"  "%dp0%\{ENTRY}" %*
""";
Test("R-shim pi shim resolves node through PATH",()=>{
 var dir=Path.Combine(temp,"pi-shim");Directory.CreateDirectory(dir);
 File.WriteAllText(Path.Combine(dir,"pi.cmd"),piShimTemplate.Replace("\r\n","\n"));File.WriteAllText(Path.Combine(dir,"pi-launcher.js"),"// stub");File.WriteAllText(Path.Combine(dir,"node.exe"),"stub");
 var pathNode=Commands.FindExe("node.exe");Assert(pathNode!=null,"node missing from PATH");
 var spec=Commands.Resolve("pi",new(){Executable=Path.Combine(dir,"pi.cmd")});
 Assert(StringComparer.OrdinalIgnoreCase.Equals(spec.Executable,pathNode),"pi shim must mirror its own PATH-node semantics, got: "+spec.Executable);
 Assert(spec.Adapter=="node-shim"&&spec.Arguments[0].EndsWith("pi-launcher.js"),"pi shim entry not translated");
});
Test("R-shim npm shim keeps sibling node preference",()=>{
 var dir=Path.Combine(temp,"npm-shim");Directory.CreateDirectory(dir);
 File.WriteAllText(Path.Combine(dir,"tool.cmd"),npmShimTemplate.Replace("{ENTRY}","tool.js").Replace("\r\n","\n"));File.WriteAllText(Path.Combine(dir,"tool.js"),"// stub");File.WriteAllText(Path.Combine(dir,"node.exe"),"stub");
 var spec=Commands.Resolve("pi",new(){Executable=Path.Combine(dir,"tool.cmd")}); Assert(StringComparer.OrdinalIgnoreCase.Equals(spec.Executable,Path.Combine(dir,"node.exe")),"npm shim lost sibling node preference");
});
Test("R-discovery searches install dirs beyond PATH",()=>{
 var dir=Path.Combine(temp,"discover");Directory.CreateDirectory(dir);
 var name="cli-"+Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(dir,name+".cmd"),"@echo off\r\n");
 var found=Commands.DiscoverInstalls(name,[dir]);
 Assert(found.Count==1&&found[0].EndsWith(name+".cmd",StringComparison.OrdinalIgnoreCase),"install dir not searched");
 Assert(Commands.DiscoverInstalls(name,[Path.Combine(temp,"discover-empty")]).Count==0,"missing CLI reported as found");
});
Test("F06 Qoder preset uses CLI rather than IDE dispatcher",()=>{
 var dir=Path.Combine(temp,"qoder-cli");Directory.CreateDirectory(dir);var exe=Path.Combine(dir,"qodercli.exe");File.WriteAllText(exe,"");File.WriteAllText(Path.Combine(dir,"qoder.cmd"),"@echo off\r\npowershell -File qoder-dispatcher.ps1 %*");
 var previous=Environment.GetEnvironmentVariable("PATH");
 try{Environment.SetEnvironmentVariable("PATH",dir);var command=Commands.Resolve("qoder",new(){Arguments=["--help"]});Assert(command.Executable==exe&&command.Adapter=="exe"&&command.Arguments.SequenceEqual(new[]{"--help"}),"Qoder resolved a dispatcher/IDE instead of the CLI");}
 finally{Environment.SetEnvironmentVariable("PATH",previous);}
});
Test("F06 discovery accepts quoted PATH without duplicate candidates",()=>{
 var dir=Path.Combine(temp,"quoted CLI");Directory.CreateDirectory(dir);var name="agent-"+Guid.NewGuid().ToString("N");var exe=Path.Combine(dir,name+".exe");File.WriteAllText(exe,"");
 var previous=Environment.GetEnvironmentVariable("PATH");
 try{Environment.SetEnvironmentVariable("PATH","\""+dir+"\"");Assert(Commands.FindExe(name)==exe,"quoted absolute PATH entry ignored");var found=Commands.DiscoverInstalls(name,[dir.ToUpperInvariant(),dir]);Assert(found.Count==1&&StringComparer.OrdinalIgnoreCase.Equals(found[0],exe),"same installation offered more than once");}
 finally{Environment.SetEnvironmentVariable("PATH",previous);}
});
Test("F12 portable templates inherit new project without weakening directory policy",()=>{
 var c=new Configuration();c.Edit(d=>{d.Project=Path.Combine(temp,"old-project");d.DirectoryMode="prepared";d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.Slots[3].Directory=Path.Combine(temp,"old-worktree");d.Slots[3].Profiles["hermes"]=new(){Arguments=["--sample",""]};});
 c.SaveTemplate("portable",portable:true);c.Edit(d=>{d.Project=Path.Combine(temp,"new-project");d.DirectoryMode="shared";});c.Restore(c.Templates["portable"]);
 Assert(c.Draft.Project==Path.Combine(temp,"new-project")&&c.Draft.DirectoryMode=="prepared"&&c.Draft.Slots.Count(s=>s.Enabled)==1&&c.Draft.Slots[3].Enabled,"portable restore lost project, isolation or selected slots");
 Assert(c.Draft.Slots.All(s=>s.Directory=="")&&c.Draft.Slots[3].Current.Arguments.SequenceEqual(new[]{"--sample",""}),"portable template retained old paths or lost arguments");
 Assert(c.Templates["portable"].Project==""&&c.Templates["portable"].Slots.All(s=>s.Directory==""),"portable snapshot contains machine-specific directories");
 Assert(c.Draft.Slots[3].RequiresDirectory,"portable restore silently dropped an explicit directory requirement");
 var restored=JsonSerializer.Deserialize<Configuration>(JsonSerializer.Serialize(c,Store.Json),Store.Json)!;Assert(restored.Templates["portable"].IsPortableTemplate&&restored.Draft.Slots[3].RequiresDirectory,"directory binding lost during persistence");
});
ResourcePolicyChecks.RunMigration(Test,temp);
ResourcePolicyChecks.RunReviewFixes(Test,temp);
ResourcePolicyChecks.RunDecisions(Test);
ResourcePolicyChecks.RunPoolAccounting(Test);
ResourcePolicyChecks.RunWaivers(Test);
ResourcePolicyChecks.RunAuthorityFixes(Test);
ConfigurationReviewChecks.Run(Test,temp);
AgentLauncher.Tests.PaneLayoutChecks.Run(Test);
AgentLauncher.Tests.TaskChecks.Run(Test,temp);
AgentLauncher.Tests.ReviewPolicyChecks.Run(Test,temp);
ReviewChecks.Run(Test,temp);
AgentLauncher.Tests.PoolPrototypeChecks.Run(Test,temp);
Console.WriteLine($"RESULT {passed.Count} passed, {failures.Count} failed. Artifacts: {temp}");
Store.Save(Path.Combine(temp,"results.json"),new{passed,failures});return failures.Count==0?0:1;
