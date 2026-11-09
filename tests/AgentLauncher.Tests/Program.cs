using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using AgentLauncher.Core;
using AgentLauncher.Windows;

if(args is ["--integration"]) return await Integration.RunAsync();
if(args.Length>0&&args[0]=="--echo") { File.WriteAllText(args[1],JsonSerializer.Serialize(new { Cwd=Environment.CurrentDirectory, Args=args.Skip(2).ToArray() }));return 0; }
if(args.Length>0&&args[0]=="--hold") {Thread.Sleep(int.Parse(args[1]));return 0;}
if(args.Length>0&&args[0]=="--allocate") {try {var data=new byte[256*1024*1024];for(var i=0;i<data.Length;i+=4096)data[i]=1;return 10;}catch(OutOfMemoryException){return 0;}}
var failures=new List<string>();var passed=new List<string>();var temp=Path.Combine(Path.GetTempPath(),"AgentLauncher-tests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(temp);
void Test(string name,Action action){try{action();passed.Add(name);Console.WriteLine("PASS "+name);}catch(Exception e){failures.Add(name+": "+e);Console.WriteLine("FAIL "+name+": "+e.Message);}}
void Assert(bool value,string message){if(!value)throw new Exception(message);}
Test("F15 profile switching",()=>{var c=new Configuration();c.Edit(d=>d.Slots[0].Profiles["pi"]=new(){Executable="pi.exe",Arguments=["--pi"]});c.Edit(d=>d.Slots[0].AgentId="codex");Assert(c.Draft.Slots[0].Current.Executable=="","stale executable");c.Edit(d=>d.Slots[0].AgentId="pi");Assert(c.Draft.Slots[0].Current.Arguments[0]=="--pi","lost profile");});
Test("F16/F17 full snapshots and LastEdited",()=>{var c=new Configuration();c.Edit(d=>{d.Slots.ForEach(s=>s.Enabled=false);d.Slots[3].Enabled=true;d.DirectoryMode="prepared";});c.SaveTemplate("a");c.Restore(new());c.Restore(new());c.Restore(c.LastEdited!);Assert(c.Draft.Slots[3].Enabled&&!c.Draft.Slots[0].Enabled&&c.Draft.DirectoryMode=="prepared","snapshot lost");c.Edit(d=>d.DirectoryMode="shared");Assert(c.Templates["a"].DirectoryMode=="prepared","snapshot aliases draft");});
Test("P low memory rejects without spawning",()=>{var s=new MemorySnapshot(16*ResourcePolicy.GiB,ResourcePolicy.GiB,32*ResourcePolicy.GiB,20*ResourcePolicy.GiB,10,DateTimeOffset.UtcNow);Assert(!ResourcePolicy.Check(s,0,ResourcePolicy.AgentBudget).Allowed,"low memory allowed");});
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
Test("G1 Job survives monitor handle close while runner retains ownership",()=>{
 var name=@"Local\AL-survive-"+Guid.NewGuid();using var root=new Job(@"Local\AL-survive-root-"+Guid.NewGuid(),root:true);var job=new Job(name,128*1024*1024);using var runnerHandle=new Job(name,openOnly:true);
 using var p=new ProtectedProcess(new(Environment.ProcessPath!,["--hold","1200"],"exe"),temp,root,job);p.Resume();job.Dispose();using var reopened=new Job(name,openOnly:true);Assert(reopened.Members().Contains(p.Process.Id),"Job lost after monitor close");Assert(p.Process.WaitForExit(4000),"child timeout");
});
Test("G1 CPU cap configured at aggregate root",()=>{using var root=new Job(@"Local\AL-cap-"+Guid.NewGuid(),root:true);var size=Marshal.SizeOf<Native.CpuRate>();var buffer=Marshal.AllocHGlobal(size);try{Native.Check(Native.QueryInformationJobObject(root.Handle,15,buffer,(uint)size,IntPtr.Zero));var rate=Marshal.PtrToStructure<Native.CpuRate>(buffer);Assert(rate.Rate==3500&&(rate.Flags&4)!=0,"CPU cap not configured");}finally{Marshal.FreeHGlobal(buffer);}});
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
 var spec=Commands.Resolve("pi",new(){Executable=Path.Combine(dir,"tool.cmd")});
 Assert(StringComparer.OrdinalIgnoreCase.Equals(spec.Executable,Path.Combine(dir,"node.exe")),"npm shim lost sibling node preference");
});
Console.WriteLine($"RESULT {passed.Count} passed, {failures.Count} failed. Artifacts: {temp}");
Store.Save(Path.Combine(temp,"results.json"),new{passed,failures});return failures.Count==0?0:1;
