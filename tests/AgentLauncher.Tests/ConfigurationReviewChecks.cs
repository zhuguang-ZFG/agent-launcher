using System.Text.Json;
using System.Text.Json.Nodes;
using AgentLauncher.Core;
using AgentLauncher.Windows;

internal static class ConfigurationReviewChecks
{
    public static void Run(Action<string,Action> test,string root)
    {
        test("R11 missing persisted execution fields cannot change restored plan",()=>
        {
            var config=new Configuration();config.Draft.Project=root;
            config.Draft.Slots=[new(){Enabled=true,AgentId="codex",Directory=root,Profiles=new(){["codex"]=new(){Executable=Path.Combine(Environment.SystemDirectory,"cmd.exe"),Arguments=["exact","", "line\nvalue"]}}}];
            config.LastEdited=config.Draft.Copy();config.SaveTemplate("saved");
            var path=Path.Combine(root,"required-execution-fields.json");Store.Save(path,config);Store.Save(path,config);
            var backup=File.ReadAllBytes(path+".bak");
            foreach(var location in new[]{"Draft","LastEdited","saved"})
            foreach(var field in new[]{"Id","Enabled","AgentId","Directory","Profiles","Executable","Arguments","Project","Revision"})
            {
                var document=JsonSerializer.SerializeToNode(config,Store.Json)!;
                var draft=location=="saved"?document["Templates"]![location]!:document[location]!;var slot=draft["Slots"]![0]!;
                var owner=field is "Executable" or "Arguments"?slot["Profiles"]!["codex"]!:field is "Project" or "Revision"?draft:slot;
                owner.AsObject().Remove(field);var bytes=JsonSerializer.SerializeToUtf8Bytes(document,Store.Json);File.WriteAllBytes(path,bytes);
                var rejected=false;
                try{var restored=Store.Read<Configuration>(path)!;restored.Draft.Validate();restored.LastEdited?.Validate();foreach(var template in restored.Templates.Values)template.Validate();}
                catch(Exception ex)when(ex is JsonException or InvalidDataException){rejected=true;}
                if(!rejected)throw new Exception($"{location}.{field} silently accepted an incomplete execution snapshot");
                if(!File.ReadAllBytes(path).SequenceEqual(bytes)||!File.ReadAllBytes(path+".bak").SequenceEqual(backup))throw new Exception("Rejected configuration or last valid backup was overwritten");
            }
        });
        test("F15 explicit defaults and complete nested profiles round trip exactly",()=>
        {
            var config=new Configuration();config.Draft.Project=root;config.Draft.Slots[1].Profiles["codex"]=new(){Executable="",Arguments=["","two\nlines","中文"]};config.LastEdited=config.Draft.Copy();config.SaveTemplate("portable",portable:true);
            var path=Path.Combine(root,"complete-execution-fields.json");Store.Save(path,config);var loaded=Store.Read<Configuration>(path)!;
            loaded.Draft.Validate();loaded.LastEdited!.Validate();loaded.Templates["portable"].Validate();
            if(JsonSerializer.Serialize(config,Store.Json)!=JsonSerializer.Serialize(loaded,Store.Json))throw new Exception("Complete/default/portable snapshots changed during round trip");
        });
    }
}
