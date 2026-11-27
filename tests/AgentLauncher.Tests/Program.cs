using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

if(args.Length==0||args.Length>0&&args[0] is "--repair-checks" or "--live-startup" or "--integration" or "--terminal-batch-checks" or "--parallel-batch-checks" or "--startup-queue-checks" or "--admission-integration" or "--remote-execution-integration" or "--sync-reliability-checks" or "--sync-reliability-integration" or "--workspace-sync" or "--remote-startup-e2e" or "--remote-live" or "--remote-live-memory" or "--remote-live-reattach" or "--live-memory-soak" or "--live-memory-child" or "--delivery-capability-check" or "--omp-handshake-checks" or "--startup-gate" or "-w" or "--check-test-isolation")
{
    try{AgentLauncher.Tests.TestIsolation.Require();}
    catch(Exception error)when(error is not OutOfMemoryException){Console.Error.WriteLine("REFUSED: "+error.Message);return 78;}
    if(args is ["--check-test-isolation"])return 0;
}

// This suite only launches the read-only guard probe and creates its own temporary fixtures.
// It may run in either build so a normal binary must prove that stateful test entry is refused.
if(args is ["--test-isolation-checks"])
{
    var isolationTemp=Path.Combine(Path.GetTempPath(),"AgentLauncher-tests-isolation-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(isolationTemp);
    var isolationPassed=0;var isolationFailed=0;
    void IsolationTest(string name,Action action){try{action();isolationPassed++;Console.WriteLine("PASS "+name);}catch(Exception error){isolationFailed++;Console.WriteLine("FAIL "+name+": "+error.Message);}}
    AgentLauncher.Tests.TestIsolationChecks.Register(IsolationTest,isolationTemp);
    Console.WriteLine($"RESULT {isolationPassed} passed, {isolationFailed} failed. Artifacts: {isolationTemp}");return isolationFailed==0?0:1;
}

if(args is ["--delivery-capability-check",var capabilityRoot])
{
    if(!capabilityRoot.Contains("AgentLauncher-tests-"))throw new IOException("Isolated root required.");
    Directory.CreateDirectory(capabilityRoot);var checks=new List<object>();
    foreach(var agent in new[]{"omp","codex","claude"})
    {
        var task=new TaskCard{AgentId=agent,Project=capabilityRoot,Name="任务传递测试",Description="只准备参数，不执行任务",DeliveryMode="automatic"};
        try{var profile=await TaskDelivery.PrepareAsync(task);var cap=await AgentProbe.ProbeCapabilityAsync(agent,new(),capabilityRoot,TimeSpan.FromSeconds(8));var resume=await ResumeCapability.CheckAsync(agent,new(),capabilityRoot,cap);checks.Add(new{agent,ok=true,cap.Version,Delivery=profile.Arguments[..^1],Resume=resume?.Arguments});Console.WriteLine("PASS capability "+agent+" "+cap.Version);}
        catch(Exception e){checks.Add(new{agent,ok=false,error=e.Message});Console.WriteLine("FAIL capability "+agent+": "+e.Message);}
    }
    Store.Save(Path.Combine(capabilityRoot,"capabilities.json"),checks);return checks.Any(c=>c.GetType().GetProperty("ok")!.GetValue(c) is false)?1:0;
}

if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_DELIVERY_FIXTURE") is {Length:>0} deliveryFixture)
{
    if(args.Contains("--help")){Console.WriteLine("Usage: fixture [PROMPT]\n --print  Non-interactive mode\n --resume <id>\n [SESSION_ID]");return 0;}
    File.WriteAllText(deliveryFixture,JsonSerializer.Serialize(args));return 0;
}

if(args.Length>=2&&args[0]=="--mode"&&args[1]=="rpc"&&Environment.GetEnvironmentVariable("AGENT_LAUNCHER_SOAK_RPC_FIXTURE") is {Length:>0} rpcFixture)
{
    Console.WriteLine("{\"type\":\"ready\",\"protocolVersion\":1,\"maxFrameBytes\":4096}");Console.Out.Flush();
    if(rpcFixture=="ready-eof")return 0;
    var line=Console.ReadLine();
    if(line is null)return 3;
    if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_SOAK_RPC_CAPTURE") is {Length:>0} rpcCapture)File.AppendAllText(rpcCapture,line+"\n");
    return 8;
}
if(args is ["--omp-handshake-checks"])
{
    var handshakeRoot=Path.Combine(Store.Root,"omp-handshake");Directory.CreateDirectory(handshakeRoot);var handshakeFailures=0;
    void HandshakeTest(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception error){handshakeFailures++;Console.WriteLine("FAIL "+name+": "+error.Message);}}
    MemoryAdmissionChecks.RunHandshakeChecks(HandshakeTest,handshakeRoot);return handshakeFailures==0?0:1;
}
if(args is ["--repair-checks"]) return RepairChecks.Run();
if(args is ["--live-startup",var startupAgent]) return await LiveStartupChecks.RunAsync(startupAgent);
if(args is ["--integration"]) return await Integration.RunAsync();
if(args is ["--terminal-batch-checks"]) return await Integration.RunAsync(terminalOnly:true);
if(args is ["--parallel-batch-checks"]) return await Integration.RunAsync(terminalOnly:true,directBatch:true);
if(args is ["--startup-queue-checks"]) return await Integration.RunAsync(startupOnly:true);
if(args is ["--admission-integration"]) return await AdmissionIntegration.RunAsync();
if(args is ["--remote-execution-integration"])return await RemoteExecutionIntegration.RunAsync();
if(args is ["--sync-review-checks"])return await AgentLauncher.Tests.SyncReviewChecks.RunAsync();
if(args is ["--sync-reliability-checks"])
{
    var syncPassed=0;var syncFailed=0;
    void SyncTest(string name,Action action){try{action();syncPassed++;Console.WriteLine("PASS "+name);}catch(Exception e){syncFailed++;Console.WriteLine("FAIL "+name+": "+e.Message);}}
    AgentLauncher.Tests.SyncReliabilityChecks.Run(SyncTest,(value,message)=>{if(!value)throw new Exception(message);});
    Console.WriteLine($"RESULT {syncPassed} passed, {syncFailed} failed.");return syncFailed==0?0:1;
}
if(args.Length is 3 or 4&&args.Take(3).SequenceEqual(new[]{"sync","list","--long"}))
    return await AgentLauncher.Tests.SyncReliabilityChecks.WriteListFixtureAsync(args.Length==4?args[3]:null);
#if INTEGRATION_TESTS
if(args is ["--sync-reliability-integration"])return await SyncReliabilityIntegration.RunAsync();
#endif
if(args is ["--remote-maintenance-checks"])return await AgentLauncher.Tests.RemoteMaintenanceChecks.RunAsync();
if(args is ["--workspace-sync"])return await WorkspaceSyncIntegration.RunAsync();
if(args is ["--remote-host-editor-checks"]){var editorFailures=0;void EditorTest(string name,Action action){try{action();Console.WriteLine("PASS "+name);}catch(Exception e){editorFailures++;Console.WriteLine("FAIL "+name+": "+e.Message);}}AgentLauncher.Tests.RemoteHostEditorChecks.Run(EditorTest,(value,message)=>{if(!value)throw new Exception(message);});return editorFailures==0?0:1;}
if(args is ["--launch-surface-checks"]) return AgentLauncher.Tests.LaunchSurfaceChecks.Run();
if(args is ["--remote-startup-e2e"])return await RemoteE2EIntegration.RunAsync();
if(args is ["--remote-live",var liveHost,var liveSshUser,var liveAgentUser,var liveDir,var liveKey,var liveArgs,var liveTimeout])return await RemoteLiveChecks.RunAsync(liveHost,liveSshUser,liveAgentUser,liveDir,liveKey,liveArgs,liveTimeout);
if(args is ["--remote-live-memory",var memHost,var memSshUser,var memAgentUser,var memDir,var memKey,var memScenario,var memMib,var memTimeout])return await RemoteLiveChecks.RunMemoryAsync(memHost,memSshUser,memAgentUser,memDir,memKey,memScenario,memMib,memTimeout);
if(args is ["--remote-live-reattach",var reHost,var reSshUser,var reAgentUser,var reDir,var reKey,var reTimeout])return await RemoteLiveChecks.RunReattachAsync(reHost,reSshUser,reAgentUser,reDir,reKey,reTimeout);
if(args is ["--allocate-commit",var mib]) return MemoryFixture.Allocate(int.Parse(mib));
if(args is ["--allocate-hold",var holdMib,var holdMs]) return MemoryFixture.AllocateHold(int.Parse(holdMib),int.Parse(holdMs));
if(args is ["--memory-cycle",var memoryPrefix]) return MemoryFixture.Cycle(memoryPrefix);
if(args is ["--memory-trim-cycle",var trimPrefix]) return MemoryFixture.Cycle(trimPrefix,verifyAfterTrim:true);
if(args is ["--spin",var spinMs]){var until=Stopwatch.GetTimestamp()+(long)(Stopwatch.Frequency*(double)int.Parse(spinMs)/1000);long spin=0;while(Stopwatch.GetTimestamp()<until)spin=(spin+1)&0xFFFF;return (int)(spin&1);}
if(args is ["--live-memory-child",var liveSpec]) return await LiveMemorySoak.Child(liveSpec);
if(args is ["--live-memory-hold",var livePrefix]) return LiveMemorySoak.Hold(livePrefix);
if(args is ["--live-memory-soak",var liveAgent,var soakCli,var soakWorker,var soakNode,var soakRepo,var soakRoot]) return await LiveMemorySoak.Run(liveAgent,soakCli,soakWorker,soakNode,soakRepo,soakRoot);
if(args is ["--guard-environment",var guardOutput])
{
    File.WriteAllText(guardOutput,JsonSerializer.Serialize(new{Pipe=Environment.GetEnvironmentVariable(OmpMemoryGuard.PipeEnvironment),Unicode=Environment.GetEnvironmentVariable("LAUNCHER_MEMORY_UNICODE"),Inherited=Environment.GetEnvironmentVariable("LAUNCHER_MEMORY_INHERITED")}));return 0;
}
if(args.Length>1&&args[0]=="-w"&&Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TERMINAL_FIXTURE") is{Length:>0} captureRoot)
{
    var pane=Array.IndexOf(args,"pane");if(pane<0||pane+1>=args.Length)return 2;
    var attempt=Guid.Parse(args[pane+1]);Directory.CreateDirectory(captureRoot);
    using(var capture=new FileStream(Path.Combine(captureRoot,attempt.ToString("N")+".json"),FileMode.CreateNew))JsonSerializer.Serialize(capture,args);
    var watch=Stopwatch.StartNew();while(!File.Exists(Path.Combine(captureRoot,attempt.ToString("N")+".release"))){if(watch.Elapsed>TimeSpan.FromSeconds(15))return 3;Thread.Sleep(20);}
    var claim=Path.Combine(Store.Root,"attempts",attempt.ToString("N"),"claim");Directory.CreateDirectory(Path.GetDirectoryName(claim)!);
    using(var file=new FileStream(claim,FileMode.CreateNew))file.WriteByte(1);
    return 0;
}
if(args.Length>0&&args[0]=="--echo") { File.WriteAllText(args[1],JsonSerializer.Serialize(new { Cwd=Environment.CurrentDirectory, Args=args.Skip(2).ToArray() }));return 0; }
if(args.Length>=3&&args[0]=="--pane-ready")
{
    var paneReadyOutput=args[1];var paneRelease=args[2];
    var pendingReady=paneReadyOutput+"."+Guid.NewGuid().ToString("N")+".tmp";
    try
    {
        using(var file=new FileStream(pendingReady,FileMode.CreateNew,FileAccess.Write,FileShare.Read)){JsonSerializer.Serialize(file,new{Pid=Environment.ProcessId,Cwd=Environment.CurrentDirectory,Args=args.Skip(3).ToArray()});file.Flush(true);}
        File.Move(pendingReady,paneReadyOutput); // Atomic visibility; never overwrite another execution's readiness proof.
    }
    finally{if(File.Exists(pendingReady))File.Delete(pendingReady);}
    var deadline=Stopwatch.StartNew();
    while(!File.Exists(paneRelease)){if(deadline.Elapsed>TimeSpan.FromSeconds(60))return 3;Thread.Sleep(50);}
    return 0;
}
if(args is ["--startup-gate",var gateReady,var gateRelease,var gateExit])
{
    using var gate=new Mutex(false,Store.RootJob+"-startup");
    if(!gate.WaitOne(TimeSpan.FromSeconds(5)))return 4;
    File.WriteAllText(gateReady,"ready");var deadline=Stopwatch.StartNew();
    while(!File.Exists(gateRelease)){if(deadline.Elapsed>TimeSpan.FromSeconds(30))return 3;Thread.Sleep(20);}
    if(gateExit=="crash")Environment.Exit(97);
    gate.ReleaseMutex();return 0;
}
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
if(args is ["--dump-env",var envPath]){var keys=new[]{"GIT_TERMINAL_PROMPT","GIT_PAGER","GCM_INTERACTIVE","GIT_OPTIONAL_LOCKS","LC_ALL","GIT_CONFIG_COUNT"};var lines=keys.Select(key=>key+"="+(Environment.GetEnvironmentVariable(key)??"")).ToList();if(int.TryParse(Environment.GetEnvironmentVariable("GIT_CONFIG_COUNT"),out var configCount))for(var i=0;i<configCount&&i<64;i++)lines.Add((Environment.GetEnvironmentVariable("GIT_CONFIG_KEY_"+i)??"")+"="+(Environment.GetEnvironmentVariable("GIT_CONFIG_VALUE_"+i)??""));File.WriteAllText(envPath,string.Join('\n',lines));return 0;}
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
// Unknown/misspelled modes used to fall through into native preparation tests. Guard that
// fallback too; a command typo must never create preparation records in production state.
if(args.Length>0&&args is not ["--soak-checks"])
{
    try{AgentLauncher.Tests.TestIsolation.Require();}
    catch(Exception error)when(error is not OutOfMemoryException){Console.Error.WriteLine("REFUSED: "+error.Message);return 78;}
}
var failures=new List<string>();var passed=new List<string>();var temp=Path.Combine(Path.GetTempPath(),"AgentLauncher-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
void Test(string name,Action action){try{action();passed.Add(name);Console.WriteLine("PASS "+name);}catch(Exception e){failures.Add(name+": "+e);Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Assert(bool value,string message){if(!value)throw new Exception(message);}
SoakChecks.Register(Test,temp);
if(args is ["--soak-checks"]){Console.WriteLine($"RESULT {passed.Count} passed, {failures.Count} failed. Artifacts: {temp}");return failures.Count==0?0:1;}
PackageChecks.Register(Test,temp);
AgentLauncher.Tests.TestIsolationChecks.Register(Test,temp);
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
Test("P01 pagination rejects an admission-state flip between frames",()=>{
    var entry=new ControlEntry{Plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),0,"page",temp,new(temp,"workspace"),false,new CommandSpec(Path.Combine(Environment.SystemDirectory,"cmd.exe"),[],"exe"),2*ResourcePolicy.GiB)};
    var first=new Response(true,"",[entry],Epoch:"page",StatusSignature:"sig",More:true);
    Response Read(Response second)
    {
        using var stream=new MemoryStream();
        Ipc.WriteAsync(stream,first,default).GetAwaiter().GetResult();
        Ipc.WriteAsync(stream,second,default).GetAwaiter().GetResult();
        stream.Position=0;return Ipc.ReadResponseAsync(stream,default).GetAwaiter().GetResult();
    }
    foreach(var second in new[]{first with{More=false,AdmissionPressure=true},first with{More=false,AdmissionHardBlockedReason="hard-blocked"}})
    {
        var rejected=false;
        try{Read(second);}catch(IOException e){rejected=e.Message=="协议分页帧不一致，已拒绝混合响应。";}
        Assert(rejected,"mixed admission state was assembled from pages");
    }
    var assembled=Read(first with{More=false});
    Assert(!assembled.More&&assembled.Entries is {Length:2},"identical pages did not assemble");
});
Test("F15 profile switching",()=>{var c=new Configuration();c.Edit(d=>d.Slots[0].Profiles["omp"]=new(){Executable="omp.exe",Arguments=["--omp"]});c.Edit(d=>d.Slots[0].AgentId="codex");Assert(c.Draft.Slots[0].Current.Executable=="","stale executable");c.Edit(d=>d.Slots[0].AgentId="omp");Assert(c.Draft.Slots[0].Current.Arguments[0]=="--omp","lost profile");});
Test("Agent catalog and pane cap match the supported product surface",()=>{
 Assert(Agents.All.Select(a=>a.Id).SequenceEqual(new[]{"omp","codex","claude"}),"unsupported Agent preset remained selectable");
 Assert(ResourcePolicy.MaxConcurrentAgents==6,"running pane cap changed");
 foreach(var id in new[]{"pi","qoder","kimi","gemini","opencode","hermes","custom"})Assert(Agents.IsKnown(id)&&!Agents.IsSupported(id),"retired identity was not preserved as read-only: "+id);
});
Test("Retired Agent commands are refused before executable resolution",()=>{
 foreach(var id in new[]{"pi","qoder","kimi","gemini","opencode","hermes","custom"})
 {
  var rejected=false;try{Commands.Resolve(id,new(){Executable=Environment.ProcessPath!});}catch(IOException){rejected=true;}
  Assert(rejected,"retired Agent resolved: "+id);
 }
});
Test("Legacy configuration disables retired slots without rewriting user fields",()=>{
 var c=new Configuration();c.Draft.Slots=[new(){Enabled=true,AgentId="pi",Directory=temp,Note="keep",Profiles=new(){["pi"]=new(){Executable="pi.exe",Arguments=["--keep"]}}},new(){Enabled=true,AgentId="codex"}];
 var accepted=ConfigurationCompatibility.Accept(c);var retired=accepted.Draft.Slots[0];
 Assert(!retired.Enabled&&retired.AgentId=="pi"&&retired.Directory==temp&&retired.Note=="keep"&&retired.Current.Executable=="pi.exe"&&retired.Current.Arguments.SequenceEqual(new[]{"--keep"}),"legacy slot was rewritten or left launchable");
 Assert(accepted.Draft.Slots[1].Enabled&&Agents.RetiredCount(accepted.Draft)==1,"supported selection or retired count changed");
});
Test("Retired task history loads but cannot be created or enqueued",()=>{
 var book=new TaskBook();var task=new TaskCard{Id=Guid.NewGuid(),Revision=1,Name="legacy",Project=temp,AgentId="pi",Profile=new(){Executable="pi.exe"},UpdatedAt=DateTimeOffset.UtcNow};book.Tasks.Add(task);book.Validate();
 var createRejected=false;try{TaskBookOps.Create(book,"new",temp,"pi",new(),"",[],"shared",null,0);}catch(InvalidDataException){createRejected=true;}
 var enqueueRejected=false;try{TaskBookOps.Enqueue(book,task.Id,new("pi.exe",[],"exe"),new string('A',64));}catch(InvalidDataException){enqueueRejected=true;}
 Assert(createRejected&&enqueueRejected,"retired task type gained new execution authority");
});
Test("F16/F17 full snapshots and LastEdited",()=>{var c=new Configuration();c.Edit(d=>{d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.DirectoryMode="prepared";});c.SaveTemplate("a");c.Restore(new());c.Restore(new());c.Restore(c.LastEdited!);Assert(c.Draft.Slots[3].Enabled&&!c.Draft.Slots[0].Enabled&&c.Draft.DirectoryMode=="prepared","snapshot lost");c.Edit(d=>d.DirectoryMode="shared");Assert(c.Templates["a"].DirectoryMode=="prepared","snapshot aliases draft");});
Test("P low memory rejects without spawning",()=>{var total=16*ResourcePolicy.GiB;MemorySnapshot Low()=>new(total,ResourcePolicy.GiB,32*ResourcePolicy.GiB,20*ResourcePolicy.GiB,10,DateTimeOffset.UtcNow);ResourcePolicySnapshot Snap(string mode)=>new(mode,ResourcePolicy.DefaultJobMemoryLimit,ResourcePolicy.DefaultPlanningBytes,ResourcePolicy.DefaultPhysicalFloor(total),ResourcePolicy.DefaultCommitFloor(32*ResourcePolicy.GiB),ResourcePolicy.DefaultCpuRate,ResourcePolicy.GitJobMemoryLimit,ResourcePolicy.DefaultGitPlanningBytes);var request=new BudgetItem[]{new(Guid.NewGuid(),"agent",ResourcePolicy.DefaultPlanningBytes,ResourcePolicy.DefaultJobMemoryLimit)};var practical=ResourcePolicy.Evaluate(Low(),Snap("practical"),[],request);Assert(practical.Kind==AdmissionKind.Allow&&practical.Advisory,"practical low memory blocked or lost its advisory: "+practical.Reason);var d=ResourcePolicy.Evaluate(Low(),Snap("conservative"),[],request);Assert(d.Kind==AdmissionKind.Deny,"conservative low memory allowed");});
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
IdleWorkingSetChecks.Run(Test,Assert);
SlotConvenienceChecks.Run(Test,Assert);
AgentLauncher.Tests.RemoteExecutionChecks.Run(Test,Assert);
AgentLauncher.Tests.RemoteReliabilityChecks.Run(Test,Assert);
AgentLauncher.Tests.SyncReliabilityChecks.Run(Test,Assert);
Test("R45 live harness rejects bad scenario and limit",()=>{
    foreach(var scenario in new[]{"","SCOPE","root","scope oom"}){var rejected=false;try{RemoteLiveChecks.GuardLive(scenario,"512");}catch(IOException){rejected=true;}Assert(rejected,"非法 scenario 被接受："+scenario);}
    foreach(var limit in new[]{"","abc","255","65537","0","-512","512.5"}){var rejected=false;try{RemoteLiveChecks.GuardLive("scope",limit);}catch(IOException){rejected=true;}Assert(rejected,"非法内存上限被接受："+limit);}
    foreach(var scenario in new[]{"scope","oom","nonroot"}){RemoteLiveChecks.GuardLive(scenario,"512");RemoteLiveChecks.GuardLive(scenario,"256");RemoteLiveChecks.GuardLive(scenario,"65536");}
});
AgentLauncher.Tests.RemoteHostEditorChecks.Run(Test,Assert);
Test("W1 idle working-set trim reclaims physical and preserves data on page-in",()=>{
 using var root=new Job(@"Local\AL-trim-root-"+Guid.NewGuid(),root:true);using var job=new Job(@"Local\AL-trim-child-"+Guid.NewGuid(),512*ResourcePolicy.MiB);
 var prefix=Path.Combine(temp,"trim-"+Guid.NewGuid().ToString("N"));
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--memory-trim-cycle",prefix],"exe"),temp,root,job);p.Resume();
 try {
 var deadline=Stopwatch.StartNew();while(!File.Exists(prefix+".allocated")){Assert(deadline.Elapsed.TotalSeconds<15,"holder did not allocate");Thread.Sleep(50);}
 var before=JobUsageReader.Read(job).PrivateWorkingSetBytes??0;Assert(before>=48UL*1024*1024,"fixture working set too small to prove trim: "+before);
 var (trimmed,skipped)=job.TrimWorkingSets();Assert(trimmed>=1&&skipped==0,$"trim did not reach the holder: trimmed={trimmed} skipped={skipped}");
 var after=JobUsageReader.Read(job).PrivateWorkingSetBytes??0;Assert(after+16UL*1024*1024<before,$"trim reclaimed too little: before={before} after={after}");
 File.WriteAllText(prefix+".touch","1");
 deadline.Restart();while(!File.Exists(prefix+".touched")){Assert(deadline.Elapsed.TotalSeconds<15,"holder could not read and rewrite its original pages after trim");Thread.Sleep(50);}
 var restored=JobUsageReader.Read(job).PrivateWorkingSetBytes??0;Assert(restored>=48UL*1024*1024,"pages did not return on use: "+restored);
 Console.WriteLine($"W1 private working set MiB: before={before/1048576.0:0.0}, after={after/1048576.0:0.0}, restored={restored/1048576.0:0.0}");
 File.WriteAllText(prefix+".free","1");
 deadline.Restart();while(!File.Exists(prefix+".freed")){Assert(deadline.Elapsed.TotalSeconds<15,"holder died after trim");Thread.Sleep(50);}
 File.WriteAllText(prefix+".exit","1");Assert(p.Process.WaitForExit(10000),"holder timeout after trim");Assert(p.Process.ExitCode==0,"holder exited abnormally after trim: "+p.Process.ExitCode);
 } finally {if(!p.Process.HasExited){p.Process.Kill();p.Process.WaitForExit(5000);}}
});
Test("ACC-A01 preset argv keeps every argument exact and isolated per agent",()=>{
 var omp=AgentPresets.For("omp",AgentPresets.OmpVerifiedVersion);
 Assert(omp.Count==5&&AgentPresets.For("omp","19.0.0").Count==0,"unknown version exposed presets");
 Assert(AgentPresets.For("omp",AgentPresets.OmpCurrentVerifiedVersion).Count==5,"current Omp version presets missing");
 var codex=AgentPresets.For("codex",AgentPresets.CodexVerifiedVersion);
 Assert(codex.Count==4&&AgentPresets.For("codex","0.167.2").Count==0,"codex presets missing or leaked to an unverified version");
 Assert(codex.Single(p=>p.Semantic=="resume-session").Fill("session 中文 with spaces").Arguments.SequenceEqual(new[]{"resume","session 中文 with spaces"}),"codex resume arguments split or changed");
 Assert(codex.Single(p=>p.Semantic=="resume-last").Arguments.SequenceEqual(new[]{"resume","--last"}),"codex continue arguments changed");
 Assert(AgentPresets.All.All(AgentPresets.StructurallyValid)&&AgentPresets.All.Select(p=>p.Id).Distinct().Count()==AgentPresets.All.Count,"preset table invalid or ids collide");
 Assert(AgentPresets.For("claude",AgentPresets.CodexVerifiedVersion).Count==0&&AgentPresets.For("codex",AgentPresets.CodexVerifiedVersion).All(p=>p.AgentId=="codex"),"preset leaked across agents");
 var claude=AgentPresets.For("claude",AgentPresets.ClaudeVerifiedVersion);
 Assert(claude.Count==4&&AgentPresets.For("claude","2.1.999").Count==0,"claude presets missing or leaked to an unverified version");
 Assert(claude.Single(p=>p.Id=="claude-continue").Arguments.SequenceEqual(new[]{"--continue"})&&claude.Single(p=>p.Id=="claude-resume").Fill("sess-1").Arguments.SequenceEqual(new[]{"--resume","sess-1"}),"claude resume argv wrong");
 foreach(var id in new[]{"pi","qoder","kimi","gemini","opencode","hermes","custom"})Assert(AgentPresets.For(id,"1.0.0").Count==0,"retired Agent exposed presets: "+id);
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
// F9 regression: the npm shim's node-selection semantics. Template text mirrors the private const in Commands.Resolve.
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
Test("R-shim npm shim keeps sibling node preference",()=>{
 var dir=Path.Combine(temp,"npm-shim");Directory.CreateDirectory(dir);
 File.WriteAllText(Path.Combine(dir,"tool.cmd"),npmShimTemplate.Replace("{ENTRY}","tool.js").Replace("\r\n","\n"));File.WriteAllText(Path.Combine(dir,"tool.js"),"// stub");File.WriteAllText(Path.Combine(dir,"node.exe"),"stub");
 var spec=Commands.Resolve("codex",new(){Executable=Path.Combine(dir,"tool.cmd")}); Assert(StringComparer.OrdinalIgnoreCase.Equals(spec.Executable,Path.Combine(dir,"node.exe")),"npm shim lost sibling node preference");
});
Test("R-discovery searches install dirs beyond PATH",()=>{
 var dir=Path.Combine(temp,"discover");Directory.CreateDirectory(dir);
 var name="cli-"+Guid.NewGuid().ToString("N");File.WriteAllText(Path.Combine(dir,name+".cmd"),"@echo off\r\n");
 var found=Commands.DiscoverInstalls(name,[dir]);
 Assert(found.Count==1&&found[0].EndsWith(name+".cmd",StringComparison.OrdinalIgnoreCase),"install dir not searched");
 Assert(Commands.DiscoverInstalls(name,[Path.Combine(temp,"discover-empty")]).Count==0,"missing CLI reported as found");
});
Test("F06 discovery accepts quoted PATH without duplicate candidates",()=>{
 var dir=Path.Combine(temp,"quoted CLI");Directory.CreateDirectory(dir);var name="agent-"+Guid.NewGuid().ToString("N");var exe=Path.Combine(dir,name+".exe");File.WriteAllText(exe,"");
 var previous=Environment.GetEnvironmentVariable("PATH");
 try{Environment.SetEnvironmentVariable("PATH","\""+dir+"\"");Assert(Commands.FindExe(name)==exe,"quoted absolute PATH entry ignored");var found=Commands.DiscoverInstalls(name,[dir.ToUpperInvariant(),dir]);Assert(found.Count==1&&StringComparer.OrdinalIgnoreCase.Equals(found[0],exe),"same installation offered more than once");}
 finally{Environment.SetEnvironmentVariable("PATH",previous);}
});
Test("F12 portable templates inherit new project without weakening directory policy",()=>{
 var c=new Configuration();c.Edit(d=>{d.Project=Path.Combine(temp,"old-project");d.DirectoryMode="prepared";d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.Slots[3].Directory=Path.Combine(temp,"old-worktree");d.Slots[3].Profiles["omp"]=new(){Arguments=["--sample",""]};});
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
MemoryAdmissionChecks.Run(Test,temp);
WorkspaceCleanupChecks.Run(Test);
LongTaskResourceChecks.Run(Test);
ResourcePolicyChecks.RunPoolAccounting(Test);
ResourcePolicyChecks.RunAuthorityFixes(Test);
ConfigurationReviewChecks.Run(Test,temp);
AgentLauncher.Tests.PaneLayoutChecks.Run(Test);
AgentLauncher.Tests.TaskChecks.Run(Test,temp);
AgentLauncher.Tests.TaskUsabilityChecks.Run(Test,temp);
AgentLauncher.Tests.TaskDailyFixChecks.Run(Test,temp);
AgentLauncher.Tests.PracticalFixChecks.Run(Test,temp);
AgentLauncher.Tests.AgentIdentityChecks.Run(Test,temp);
AgentLauncher.Tests.ReviewPolicyChecks.Run(Test,temp);
ReviewChecks.Run(Test,temp);
AgentLauncher.Tests.PoolPrototypeChecks.Run(Test,temp);
Console.WriteLine($"RESULT {passed.Count} passed, {failures.Count} failed. Artifacts: {temp}");
Store.Save(Path.Combine(temp,"results.json"),new{passed,failures});return failures.Count==0?0:1;
