#if INTEGRATION_TESTS
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private async Task RunMultipaneChecksAsync(Action<string,bool> check)
    {
        if(Environment.GetEnvironmentVariable("AGENT_LAUNCHER_TEST_ROOT") is not { } testRoot||!testRoot.Contains("AgentLauncher-tests-",StringComparison.OrdinalIgnoreCase))throw new IOException("多窗格检查必须使用隔离测试根。");
        await saveTask;await commandCheckTask;
        var originalConfig=config;var originalStatus=status;var originalConfirm=TestConfirm;
        var originalBusy=busy;var originalReadOnly=readOnly;var originalLoading=loading;var originalTimer=timer.IsEnabled;
        var originalAttempts=submittedAttempts;var originalRevision=submittedRevision;var originalError=ErrorText.Text;var originalProgress=ProgressText.Text;
        var originalDefaults=presetSlotDefaults.ToDictionary(p=>p.Key,p=>p.Value.Copy());var originalEdited=presetEditedSlots.ToArray();
        timer.Stop();busy=false;readOnly=false;loading=false;submittedAttempts=[];submittedRevision=-1;presetSlotDefaults.Clear();presetEditedSlots.Clear();
        MemorySnapshot Sample()=>new(32*ResourcePolicy.GiB,28*ResourcePolicy.GiB,64*ResourcePolicy.GiB,60*ResourcePolicy.GiB,5,DateTimeOffset.UtcNow);
        Configuration Fixture()=>new(){Draft=new(){Project=Store.Root,DirectoryMode="shared"}};
        ControlEntry Entry(string state,int position,string kind="agent")=>new(){Plan=new(Guid.NewGuid(),Guid.NewGuid(),position,"multipane-check",Store.Root,new(Store.Root,"multipane-check"),false,new(Path.Combine(Environment.SystemDirectory,"cmd.exe"),[],"exe"),kind=="git"?ResourcePolicy.GitJobMemoryLimit:ResourcePolicy.DefaultJobMemoryLimit,Kind:kind,PlanningBytes:kind=="git"?ResourcePolicy.DefaultGitPlanningBytes:ResourcePolicy.DefaultPlanningBytes),State=state,Detail=state};
        void Healthy(ControlEntry[]? entries=null)=>status=new(true,"multipane-check",entries??[],Sample(),Epoch:"multipane-check");
        try
        {
            config=Fixture();Healthy();Render();
            var firstFour=config.Draft.Slots.Select(s=>s.Copy()).ToArray();
            ApplyScalePreset(16);
            check("ACC-L01 preset appends enabled slots without changing original slots",config.Draft.Slots.Count==16&&config.Draft.Slots.Select(s=>s.Id).Distinct().Count()==16&&config.Draft.Slots.Skip(4).All(s=>s.Enabled)&&firstFour.Select(s=>JsonSerializer.Serialize(s,Store.Json)).SequenceEqual(config.Draft.Slots.Take(4).Select(s=>JsonSerializer.Serialize(s,Store.Json))));
            config.Edit(d=>{for(var i=0;i<d.Slots.Count;i++){d.Slots[i].Enabled=i is 0 or 7 or 15;d.Slots[i].Profiles[d.Slots[i].AgentId]=new(){Executable=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments=[]};}});
            Healthy([Entry("Pending",1),Entry("Started",2),Entry("Creating",3),Entry("Unknown",4),Entry("Started",0,"git"),Entry("Settled",5)]);Render();
            await commandCheckTask;await saveTask;
            UpdateMultipaneSummary(DecideAdmission(config.Draft,status!.Memory,status.Entries));
            check("ACC-S01 four counts with 16 configured and 3 enabled",config.Draft.Slots.Count==16&&config.Draft.Slots.Count(s=>s.Enabled)==3&&ConfiguredCountValue.Text=="16"&&EnabledCountValue.Text=="3"&&ReservedCountValue.Text=="4"&&StartedCountValue.Text=="1"&&LaunchButton.Content is string launchLabel&&launchLabel.Contains("3")&&Preview.Children.OfType<Grid>().Sum(g=>g.Children.Count)==3);
            check("ACC-S01 creating and unknown are not reported as started",CreatedPendingText.Visibility==Visibility.Visible&&CreatedPendingText.Text.Contains("创建中 1")&&CreatedPendingText.Text.Contains("待核对 1")&&StartedCountValue.Text=="1");
            status=new(false,"offline",Memory:Sample());UpdateMultipaneSummary(DecideAdmission(config.Draft,status.Memory,status.Entries));
            check("ACC-S01 unavailable status does not fabricate zero reservations",ReservedCountValue.Text=="—"&&StartedCountValue.Text=="—");
            Healthy();

            foreach(var (n,layout,rows) in new (int,string,int[])[]{(2,"horizontal",[2]),(3,"vertical",[1,2]),(6,"vertical",[2,2,1,1]),(8,"vertical",[2,2,2,2]),(16,"vertical",[4,4,4,4])})
            {
                config.Draft.Layout=layout;var active=config.Draft.Slots.Take(n).ToArray();RenderMultipanePreview(active);
                Preview.Measure(new Size(400,320));Preview.Arrange(new Rect(0,0,400,320));
                var panes=FindLogical(Preview).OfType<Border>().Where(pane=>pane.Tag is PaneCell).ToArray();
                var correct=panes.Length==n;var enabledIndex=0;
                for(var column=0;column<rows.Length;column++)for(var row=0;row<rows[column];row++)
                {
                    var currentIndex=enabledIndex++;var pane=panes.SingleOrDefault(p=>p.Tag is PaneCell cell&&cell.EnabledIndex==currentIndex);
                    if(pane==null){correct=false;continue;}
                    var point=pane.TranslatePoint(new Point(),Preview);
                    correct&=Math.Abs(point.X-(400d*column/rows.Length+pane.Margin.Left))<1&&Math.Abs(point.Y-(320d*row/rows[column]+pane.Margin.Top))<1;
                    correct&=Math.Abs(pane.ActualWidth-(400d/rows.Length-pane.Margin.Left-pane.Margin.Right))<1&&Math.Abs(pane.ActualHeight-(320d/rows[column]-pane.Margin.Top-pane.Margin.Bottom))<1;
                }
                check($"ACC-L03 preview {n} {layout} renders packed equal columns and rows",correct);
            }
            RenderMultipanePreview([]);check("ACC-L03 zero enabled preview has no pane placeholders",!FindLogical(Preview).OfType<Border>().Any(pane=>pane.Tag is PaneCell));

            config=Fixture();presetSlotDefaults.Clear();presetEditedSlots.Clear();Healthy();Render();ApplyScalePreset(16);
            Edit(d=>d.Slots[9].Directory=Path.Combine(Store.Root,"keep-edited-directory"));
            Edit(d=>d.Slots[10].Note="keep-edited-note");
            Edit(d=>d.Slots[11].Profiles[d.Slots[11].AgentId]=new(){Executable=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments=["--kept"]});
            Edit(d=>d.Slots[12].Enabled=false);Edit(d=>d.Slots[12].Enabled=true);
            Edit(d=>d.Slots[13].Note="temporary edit");Edit(d=>d.Slots[13].Note="");
            var beforeShrink=config.Draft.Copy();ApplyScalePreset(8);
            check("ACC-L04 shrink preset keeps edited slot",config.Draft.Slots.Count==16&&config.Draft.Slots.Select(s=>s.Id).SequenceEqual(beforeShrink.Slots.Select(s=>s.Id))&&config.Draft.Slots[9].Directory==beforeShrink.Slots[9].Directory&&config.Draft.Slots[9].Enabled&&config.Draft.Slots[10].Note=="keep-edited-note"&&config.Draft.Slots[11].Current.Arguments.SequenceEqual(new[]{"--kept"})&&config.Draft.Slots[12].Enabled&&config.Draft.Slots[13].Enabled&&!config.Draft.Slots[8].Enabled&&!config.Draft.Slots[14].Enabled&&!config.Draft.Slots[15].Enabled&&ProgressText.Text.Contains("第 10 项"));
            var enabledAfterShrink=config.Draft.Slots.Select(s=>s.Enabled).ToArray();ApplyScalePreset(16);
            check("ACC-L04 enlarging existing capacity never silently reenables old slots",config.Draft.Slots.Select(s=>s.Enabled).SequenceEqual(enabledAfterShrink));

            config=Fixture();ApplyScalePreset(8);config.Edit(d=>{for(var i=0;i<d.Slots.Count;i++){var slot=d.Slots[i];slot.AgentId="pi";slot.Directory=Path.Combine(Store.Root,$"slot-{i}");slot.RequiresDirectory=i%2==0;slot.Note=$"note-{i}";slot.CustomName=$"name-{i}";slot.Enabled=i%3==0;slot.Profiles["codex"]=new(){Executable="keep-codex",Arguments=[$"keep-{i}"]};}d.Slots[2].AgentId="claude";d.Slots[2].Profiles["claude"]=new(){Executable="source-cli",Arguments=["--source","argument with spaces"]};});Render();
            var sourceId=config.Draft.Slots[2].Id;var beforeBatch=config.Draft.Copy();ApplyAgentToSlots(sourceId,false);
            bool UnrelatedFieldsKept()=>config.Draft.Slots.Select((slot,index)=>slot.Id==beforeBatch.Slots[index].Id&&slot.Directory==beforeBatch.Slots[index].Directory&&slot.RequiresDirectory==beforeBatch.Slots[index].RequiresDirectory&&slot.Note==beforeBatch.Slots[index].Note&&slot.CustomName==beforeBatch.Slots[index].CustomName&&slot.Enabled==beforeBatch.Slots[index].Enabled&&slot.Profiles["codex"].Arguments.SequenceEqual(beforeBatch.Slots[index].Profiles["codex"].Arguments)).All(value=>value);
            check("ACC-L05 batch apply does not copy directory or note",UnrelatedFieldsKept()&&config.Draft.Slots.Take(2).All(s=>s.AgentId=="pi")&&config.Draft.Slots.Skip(3).All(s=>s.AgentId=="claude"&&s.Current.Executable=="source-cli"&&s.Current.Arguments.SequenceEqual(new[]{"--source","argument with spaces"})));
            var batchSource=config.Draft.Slots[2].Current;var batchTarget=config.Draft.Slots[3].Current;var batchSibling=config.Draft.Slots[4].Current;batchTarget.Arguments[0]="target-only";
            check("ACC-L05 batch profiles and argument arrays are independent",!ReferenceEquals(batchSource,batchTarget)&&!ReferenceEquals(batchTarget.Arguments,batchSibling.Arguments)&&batchSource.Arguments[0]=="--source"&&batchSibling.Arguments[0]=="--source");
            ApplyAgentToSlots(sourceId,true);check("ACC-L05 applying to all includes preceding slots and preserves identity",UnrelatedFieldsKept()&&config.Draft.Slots.All(s=>s.AgentId=="claude"&&s.Current.Arguments.SequenceEqual(new[]{"--source","argument with spaces"})));

            var waiverItems=new[]{Entry("Pending",1).Plan with{Name="secret-name",Cwd="secret-directory",Command=new("secret-executable",["secret-argument"],"exe")},Entry("Pending",16).Plan};
            var sample=Sample();var waiver=new AdmissionDecision(AdmissionKind.RequireWaiver,"soft only",2*ResourcePolicy.DefaultPlanningBytes,ResourcePolicy.DefaultPlanningBytes,4*ResourcePolicy.GiB,2*ResourcePolicy.GiB,5*ResourcePolicy.GiB,2*ResourcePolicy.GiB,2*ResourcePolicy.GiB,true);
            var prompts=0;var confirmationText="";TestConfirm=(text,_,buttons)=>{prompts++;confirmationText=text;check("ACC-R01 waiver asks explicit yes no",buttons==MessageBoxButton.YesNo);return MessageBoxResult.No;};
            check("ACC-R02 deny path has no waiver prompt",!ConfirmResourceWaiver(waiverItems,waiver with{Kind=AdmissionKind.Deny},sample)&&prompts==0);
            check("ACC-R02 invalid sample has no waiver prompt",!ConfirmResourceWaiver(waiverItems,waiver,sample with{At=sample.At.AddSeconds(-10)})&&prompts==0);
            check("ACC-R01 declining waiver stops before authorization",!ConfirmResourceWaiver(waiverItems,waiver,sample)&&prompts==1);
            check("ACC-R01 waiver prompt lists scope expiry sampling and hard floors without secrets",confirmationText.Contains("第 01 项")&&confirmationText.Contains("第 16 项")&&confirmationText.Contains("45 秒")&&confirmationText.Contains("不能突破")&&confirmationText.Contains("不可复用")&&confirmationText.Contains("采样时间")&&confirmationText.Contains("物理")&&confirmationText.Contains("提交")&&confirmationText.Contains("软超差额")&&!confirmationText.Contains("secret-"));
            TestConfirm=(_,_,_)=>MessageBoxResult.Yes;check("ACC-R01 accepting waiver returns consent without issuing IPC",ConfirmResourceWaiver(waiverItems,waiver,Sample()));
            check("ACC-R01 Git preparation can request the same protected confirmation",ConfirmResourceWaiver([Entry("Pending",1,"git").Plan],waiver,Sample()));

            var resourceDialog=new ResourceSettingsWindow(this,new ResourceSettings(),Sample(),false,false);
            check("ACC-U03 practical values are read only until custom selected",resourceDialog.JobLimitGiB.IsReadOnly&&resourceDialog.PlanningMiB.IsReadOnly);
            resourceDialog.ModeChoice.SelectedIndex=2;resourceDialog.JobLimitGiB.Text="NaN";
            check("ACC-U03 invalid numeric input is rejected inside resource dialog",!resourceDialog.TryApplySettings()&&resourceDialog.Value is null&&resourceDialog.ValidationMessage.Text.Length>0);
            resourceDialog.JobLimitGiB.Text="1";resourceDialog.PlanningMiB.Text="1536";
            check("ACC-U03 planning above Job cap is rejected without clamping",!resourceDialog.TryApplySettings()&&resourceDialog.Value is null&&resourceDialog.PlanningMiB.Text=="1536");
            resourceDialog.JobLimitGiB.Text="2";resourceDialog.PlanningMiB.Text="768";resourceDialog.PhysicalFloorGiB.Text="0";
            check("ACC-U03 zero hard floor is rejected",!resourceDialog.TryApplySettings()&&resourceDialog.Value is null);
            resourceDialog.PhysicalFloorGiB.Text=(1.5m).ToString(System.Globalization.CultureInfo.CurrentCulture);resourceDialog.CommitFloorGiB.Text="1";resourceDialog.CpuPercent.Text="35";
            check("ACC-U03 valid custom values use 1024 based units",resourceDialog.TryApplySettings()&&resourceDialog.Value is {Mode:"custom",JobMemoryLimit:2*ResourcePolicy.GiB,PlanningBytes:768*ResourcePolicy.MiB,CpuRate:3500} settings&&settings.PhysicalFloorOverride==ResourcePolicy.MinCustomPhysicalFloor&&settings.CommitFloorOverride==ResourcePolicy.MinCustomCommitFloor);
            resourceDialog.ModeChoice.SelectedIndex=1;resourceDialog.TryApplySettings();
            check("ACC-U03 leaving custom resets resource defaults",resourceDialog.Value is {Mode:"practical",JobMemoryLimit:ResourcePolicy.DefaultJobMemoryLimit,PlanningBytes:ResourcePolicy.DefaultPlanningBytes,CpuRate:ResourcePolicy.DefaultCpuRate,PhysicalFloorOverride:null,CommitFloorOverride:null});resourceDialog.Close();
            var activeDialog=new ResourceSettingsWindow(this,new ResourceSettings{Mode="custom",JobMemoryLimit=3*ResourcePolicy.GiB},Sample(),true,false);
            activeDialog.JobLimitGiB.Text="4";activeDialog.TryApplySettings();
            check("ACC-U03 active batches allow future memory limits but lock shared CPU",activeDialog.ModeChoice.IsEnabled&&!activeDialog.JobLimitGiB.IsReadOnly&&activeDialog.CpuPercent.IsReadOnly&&activeDialog.Value?.JobMemoryLimit==4*ResourcePolicy.GiB);
            activeDialog.CpuPercent.Text="40";
            check("ACC-U03 programmatic CPU edit cannot bypass active protection",!activeDialog.TryApplySettings()&&activeDialog.Value is null);activeDialog.Close();
            var poolDialog=new ResourceSettingsWindow(this,new ResourceSettings(),Sample(),false,false);
            check("ACC-U03 pool mode defaults to independent with no pool limit",Equals(poolDialog.PoolChoice.SelectedValue,ResourcePolicy.PoolIndependent)&&string.IsNullOrEmpty(poolDialog.PoolLimitGiB.Text));
            poolDialog.ModeChoice.SelectedIndex=2;poolDialog.PoolChoice.SelectedValue=ResourcePolicy.PoolShared;
            check("ACC-U03 selecting shared fills a pool limit at least the item cap",poolDialog.Value is null&&ulong.TryParse(poolDialog.PoolLimitGiB.Text,System.Globalization.NumberStyles.AllowDecimalPoint,System.Globalization.CultureInfo.CurrentCulture,out var filled)&&filled>=2);
            poolDialog.JobLimitGiB.Text="4";poolDialog.PoolLimitGiB.Text="2";
            check("ACC-U03 pool limit below the item cap is rejected",!poolDialog.TryApplySettings()&&poolDialog.Value is null&&poolDialog.ValidationMessage.Text.Length>0);
            poolDialog.PoolLimitGiB.Text="6";poolDialog.PhysicalFloorGiB.Text="";poolDialog.CommitFloorGiB.Text="";poolDialog.CpuPercent.Text="35";
            check("ACC-U03 shared pool limit is written as 1024 based bytes",poolDialog.TryApplySettings()&&poolDialog.Value is {Pool:ResourcePolicy.PoolShared,PoolMemoryLimit:6UL*ResourcePolicy.GiB} sharedValue&&sharedValue.JobMemoryLimit==4*ResourcePolicy.GiB);
            poolDialog.ModeChoice.SelectedIndex=1;poolDialog.TryApplySettings();
            check("ACC-U03 leaving custom keeps the pool choice and limit",poolDialog.Value is {Mode:"practical",Pool:ResourcePolicy.PoolShared,PoolMemoryLimit:6UL*ResourcePolicy.GiB});poolDialog.Close();

            var v1Ledger=new Ledger{SchemaVersion=1,BootId="ui-check"};v1Ledger.Entries.Add(new(){Plan=new LaunchItem(Guid.NewGuid(),Guid.NewGuid(),1,"legacy",Store.Root,PhysicalDirectory.Workspace(Store.Root),false,new CommandSpec(Path.Combine(Environment.SystemDirectory,"cmd.exe"),[],"exe"),ResourcePolicy.AgentBudget),State="Settled"});
            check("ACC-S3A08 a version 1 terminal ledger needs no monitor recovery",!LedgerNeedsRecovery(v1Ledger));
            var poolLedger=new Ledger{SchemaVersion=2,BootId="ui-check",Pools=[new(){Id=Guid.NewGuid(),MemoryLimit=4*ResourcePolicy.GiB,PolicyHash="HASH",BatchId=Guid.NewGuid()}]};
            check("ACC-S3A08 retained pools force recovery even without active entries",LedgerNeedsRecovery(poolLedger));
            var futureLedger=new Ledger{SchemaVersion=3,BootId="ui-check"};var futureRejected=false;
            try{LedgerNeedsRecovery(futureLedger);}catch(InvalidDataException){futureRejected=true;}
            check("ACC-S3A08 an unknown ledger version still fails closed",futureRejected);
            var brokenLedger=new Ledger{SchemaVersion=2,BootId="ui-check"};brokenLedger.Entries.Add(new(){Plan=null!});var brokenRejected=false;
            try{LedgerNeedsRecovery(brokenLedger);}catch(Exception error)when(error is InvalidDataException or NullReferenceException){brokenRejected=true;}
            check("ACC-S3A08 a ledger entry without a plan fails closed",brokenRejected);
            config=Fixture();Healthy();Render();var dropped=Enumerable.Repeat(Store.Root,ResourcePolicy.MaxSlots).ToArray();await ApplyDroppedDirectoriesAsync(dropped,null,config.Draft.Revision);
            check("ACC-L02 sixteen dropped directories are applied atomically",config.Draft.Slots.Count==ResourcePolicy.MaxSlots&&config.Draft.Slots.All(s=>s.Directory==Store.Root));
            var dropRevision=config.Draft.Revision;var dropSnapshot=JsonSerializer.Serialize(config.Draft,Store.Json);var rejected=false;
            try{await ApplyDroppedDirectoriesAsync([..dropped,Store.Root],null,dropRevision);}catch(IOException){rejected=true;}
            check("ACC-L02 seventeenth dropped directory leaves configuration untouched",rejected&&config.Draft.Revision==dropRevision&&JsonSerializer.Serialize(config.Draft,Store.Json)==dropSnapshot);
        }
        finally
        {
            await commandCheckTask;await saveTask;
            config=originalConfig;status=originalStatus;TestConfirm=originalConfirm;busy=originalBusy;readOnly=originalReadOnly;loading=originalLoading;submittedAttempts=originalAttempts;submittedRevision=originalRevision;
            presetSlotDefaults.Clear();foreach(var pair in originalDefaults)presetSlotDefaults[pair.Key]=pair.Value;presetEditedSlots.Clear();presetEditedSlots.UnionWith(originalEdited);
            Render();RenderSessionsNow();await commandCheckTask;QueueSave();await saveTask;ErrorText.Text=originalError;ProgressText.Text=originalProgress;if(originalTimer)timer.Start();
        }
    }
}
#endif
