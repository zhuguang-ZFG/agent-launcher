using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AgentLauncher.Core;
using AgentLauncher.Windows;

namespace AgentLauncher.App;
public partial class MainWindow
{
    private const string SlotDragFormat="AgentLauncher.Slot";
    private sealed record SlotDrag(Guid Id,long Revision);
    private bool checkingDrop;
    private async Task ApplyDroppedDirectoriesAsync(string[] paths,Guid? slotId,long revision)
    {
        if(busy||readOnly||checkingDrop)return;
        if(paths.Length is <1 or >4||slotId!=null&&paths.Length!=1)throw new IOException("顶部最多接收 4 个文件夹；单项只能接收一个文件夹。当前配置未更改。");
        if(ProjectPath.Text!=config.Draft.Project)throw new IOException("项目路径正在编辑，请先完成输入再拖入目录；已保留输入。");
        var inputRevision=projectInputRevision;
        checkingDrop=true;
        try
        {
            var verified=await Task.Run(()=>paths.Select(p=>PhysicalDirectory.Resolve(p).Path).ToArray());
            if(revision!=config.Draft.Revision||inputRevision!=projectInputRevision)throw new IOException("检查目录期间配置或路径输入已改变，请重新拖入；没有覆盖新输入。");
            if(slotId is {} id)Edit(d=>{var slot=d.Slots.Single(s=>s.Id==id);slot.Directory=verified[0];slot.RequiresDirectory=false;});
            else if(verified.Length==1)SelectProject(verified[0]);
            else Edit(d=>{while(d.Slots.Count<verified.Length)d.Slots.Add(new());for(var i=0;i<verified.Length;i++){d.Slots[i].Directory=verified[i];d.Slots[i].RequiresDirectory=false;}});
            ProgressText.Text=$"已填入 {verified.Length} 个目录，本次启用 {config.Draft.Slots.Count(s=>s.Enabled)} 项；不会自动启动。";
        }
        finally{checkingDrop=false;}
    }
    private void AttachSlotDrag(Border container,FrameworkElement handle,TextBlock directory,Slot slot,int position)
    {
        Point? start=null;handle.Cursor=Cursors.SizeAll;handle.ToolTip="拖动调整顺序，也可使用更多操作中的上移/下移";
        handle.PreviewMouseLeftButtonDown+=(_,e)=>start=e.GetPosition(handle);
        handle.PreviewMouseLeftButtonUp+=(_,_)=>start=null;
        handle.PreviewMouseMove+=(_,e)=>{
            if(start is not {} point||e.LeftButton!=MouseButtonState.Pressed||busy||readOnly)return;var current=e.GetPosition(handle);
            if(Math.Abs(current.X-point.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(current.Y-point.Y)<SystemParameters.MinimumVerticalDragDistance)return;
            start=null;DragDrop.DoDragDrop(handle,new DataObject(SlotDragFormat,new SlotDrag(slot.Id,config.Draft.Revision)),DragDropEffects.Move);
        };
        container.AllowDrop=true;
        var original=directory.Text;
        void Clear(){container.Background=Brushes.Transparent;directory.Text=original;}
        container.DragOver+=(_,e)=>{
            var move=e.Data.GetData(SlotDragFormat) is SlotDrag drag&&drag.Revision==config.Draft.Revision;
            var folders=e.Data.GetDataPresent(DataFormats.FileDrop);
            e.Effects=!busy&&!readOnly&&!checkingDrop&&(move||folders)?move?DragDropEffects.Move:DragDropEffects.Copy:DragDropEffects.None;
            if(e.Effects!=DragDropEffects.None){container.SetResourceReference(Border.BackgroundProperty,"Selected");directory.Text=move?$"移动到第 {position} 项的位置":"替换此项目录";}e.Handled=true;
        };
        container.DragLeave+=(_,_)=>Clear();
        container.Drop+=async(_,e)=>{
            e.Handled=true;Clear();if(busy||readOnly)return;
            if(e.Data.GetData(SlotDragFormat) is SlotDrag drag)
            {
                if(drag.Revision!=config.Draft.Revision||drag.Id==slot.Id)return;
                Edit(d=>{var source=d.Slots.Single(s=>s.Id==drag.Id);var target=d.Slots.FindIndex(s=>s.Id==slot.Id);d.Slots.Remove(source);d.Slots.Insert(Math.Min(target,d.Slots.Count),source);});return;
            }
            if(e.Data.GetData(DataFormats.FileDrop) is string[] paths)await GuardAsync(()=>ApplyDroppedDirectoriesAsync(paths,slot.Id,config.Draft.Revision));
        };
    }
}
