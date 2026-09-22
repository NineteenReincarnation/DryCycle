using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DryCycle.DevUI.DevTool.Core;
using DryCycle.DevUI.DevTool.History;

namespace DryCycle.DevUI.DevTool.Map.Cartography;

internal static partial class CartographyRuntime
{
    private sealed class SourceResult { internal CartographySource Source; internal CartographyDocument Initial; internal string Identity,Region,Campaign,Installation; internal string[] Warnings=Array.Empty<string>(); }
    private static Task<SourceResult> loadingSource;
    private static string sourceStatus="";
    private static string[] regions=Array.Empty<string>(),fonts=Array.Empty<string>(),icons=Array.Empty<string>();
    private static string installation="",campaign="White";
    private static bool independentRegion;
    internal static string[] Regions=>regions;
    internal static string[] Fonts=>fonts;
    internal static string[] Icons=>icons;
    internal static bool LoadingSource=>loadingSource!=null;

    private static void ProcessSources(EditorSession session)
    {
        currentSession=session;
        bool worldChanged=!ReferenceEquals(observedWorld,session.World);observedWorld=session.World;
        if(loadingSource!=null)
        {
            if(!loadingSource.IsCompleted)return;
            try
            {
                SourceResult loaded=loadingSource.GetAwaiter().GetResult();
                CartographyGameAssets.Load(loaded.Source);
                if(!Documents.TryGetValue(loaded.Identity,out Workspace workspace))
                {
                    workspace=new Workspace{Identity=loaded.Identity,Path=ProjectPath(loaded.Identity,loaded.Region),Source=loaded.Source};
                    workspace.History.ActivateDocument(new EditorDocumentKey(EditorDocumentKind.RegionMap,"Cartography:"+loaded.Identity));
                    if(File.Exists(workspace.Path))
                    {byte[] bytes=File.ReadAllBytes(workspace.Path);workspace.Document=CartographyStorage.Deserialize(System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'),loaded.Identity);workspace.DiskHash=CartographyStorage.HashBytes(bytes);workspace.SavedText=CartographyStorage.Serialize(workspace.Document);}
                    else workspace.Document=loaded.Initial??loaded.Source.CreateDocument(loaded.Identity,loaded.Region);
                    workspace.Document.Options.Campaign=loaded.Campaign;workspace.Document.Options.Installation=loaded.Installation;
                    workspace.AuthorText=CartographyStorage.Serialize(workspace.Document);Documents.Add(workspace.Identity,workspace);
                }
                workspace.Source=loaded.Source;workspace.Scene=CartographySceneBuilder.Build(workspace.Document,workspace.Source,workspace.SceneCache);
                workspace.Status=loaded.Warnings.Length>0?string.Join("\n",loaded.Warnings):"Loaded / 已载入 "+loaded.Region+" · "+loaded.Source.Rooms.Count+" rooms";
                current=workspace;installation=loaded.Installation;campaign=loaded.Campaign;sourceStatus="";
                fonts=CartographyAssets.FontNames;icons=CartographyAssets.IconNames;Publish(workspace);EditorRevisionHub.Mark(session,EditorRevisionKind.Shell);
            }
            catch(Exception error){sourceStatus="Region load failed / 区域读取失败: "+error.Message;Plugin.Logger?.LogError(sourceStatus+"\n"+error);if(current!=null){current.Status=sourceStatus;Publish(current);}else presentation=new CartographyPresentation{Status=sourceStatus};}
            finally{loadingSource=null;}
        }
        else if((current==null&&sourceStatus.Length==0)||(worldChanged&&!independentRegion))
            RequestSource(session.World.name,session.World.game?.StoryCharacter?.value??"White","",false);
    }

    private static void RequestSource(string region,string selectedCampaign,string path,bool cornifer)
    {
        if(loadingSource!=null)throw new InvalidOperationException("A region is already loading.");
        sourceStatus="Loading / 正在读取区域…";
        if(current!=null){current.Status=sourceStatus;Publish(current);}else presentation=new CartographyPresentation{Status=sourceStatus};
        string defaultRoot=Path.Combine(RWCustom.Custom.RootFolderDirectory(),"RainWorld_Data","StreamingAssets");
        string root=string.IsNullOrWhiteSpace(path)||cornifer?defaultRoot:Path.GetFullPath(path);
        if(File.Exists(root))root=Path.GetDirectoryName(root);
        if(Directory.Exists(Path.Combine(root,"RainWorld_Data","StreamingAssets")))root=Path.Combine(root,"RainWorld_Data","StreamingAssets");
        if(!Directory.Exists(root))throw new DirectoryNotFoundException("Installation not found: "+root);
        List<string> roots=new(){Path.Combine(root,"mergedmods")};
        if(string.Equals(root,defaultRoot,StringComparison.OrdinalIgnoreCase))
            roots.AddRange(ModManager.ActiveMods.AsEnumerable().Reverse().Select(m=>m.path));
        roots.Add(root);string[] resolverRoots=roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        regions=resolverRoots.Select(p=>Path.Combine(p,"world")).Where(Directory.Exists).SelectMany(p=>Directory.EnumerateDirectories(p))
            .Where(p=>File.Exists(Path.Combine(p,"world_"+Path.GetFileName(p)+".txt"))).Select(Path.GetFileName).Select(s=>s.ToUpperInvariant()).Distinct().OrderBy(s=>s).ToArray();
        string localCornifer=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"Cornifer");
        loadingSource=Task.Run(()=>
        {
            if(Directory.Exists(localCornifer))CartographyAssets.LoadDirectory(localCornifer);
            if(cornifer)
            {
                if(!string.Equals(Path.GetDirectoryName(path),localCornifer,StringComparison.OrdinalIgnoreCase))CartographyAssets.LoadDirectory(Path.GetDirectoryName(path));
                var imported=CartographyCorniferImport.Load(path);
                return new SourceResult{Source=imported.Source,Initial=imported.Document,Identity=imported.Document.Identity,Region=imported.Document.Region,Campaign=imported.Document.Options.Campaign,Installation=root,Warnings=imported.Warnings};
            }
            string Read(string relative){foreach(string folder in resolverRoots){string file=Path.Combine(folder,relative.Replace('/',Path.DirectorySeparatorChar));if(File.Exists(file))return File.ReadAllText(file);}return null;}
            CartographySource source=CartographyRegionLoader.Load(region,selectedCampaign,Read);
            return new SourceResult{Source=source,Identity="region:"+string.Join(";",resolverRoots).ToLowerInvariant()+"|"+region+"|"+selectedCampaign,Region=region,Campaign=selectedCampaign,Installation=root};
        });
    }
}
