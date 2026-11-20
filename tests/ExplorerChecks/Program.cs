using FileInventory;
using Microsoft.Data.Sqlite;
static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
string root=args[0],data=args[1];
if (Directory.EnumerateFileSystemEntries(root).Any() || Directory.Exists(data))
    throw new Exception("Use an empty disposable drive and a new catalog directory.");
Directory.CreateDirectory(Path.Combine(root,"Projects","Empty"));
Directory.CreateDirectory(Path.Combine(root,"Projects","Reports"));
for(int i=0;i<625;i++) File.WriteAllText(Path.Combine(root,"Projects","Reports",$"Needle-{i:D4}.txt"),"sample");
File.WriteAllText(Path.Combine(root,"Projects","Café (Q4).pdf"),"sample");
var catalog=new Catalog(data);
string id=WindowsVolumes.Identity(root)??throw new Exception("Fixture volume not mounted");
var volume=new Volume(id,root,"Explorer test",true,false,24,null,null,"","");
catalog.Discover([volume]);
await new Scanner(catalog).ScanAsync(volume,new Progress<ScanProgress>(),CancellationToken.None);
Check(catalog.Volumes().Single().Outcome=="Complete","real Windows scanner publishes files and directories");
var page=catalog.QueryPage("needle",new(Volume:id),0,256);
Check(page.Total==625 && page.Rows.Count==256,"625 matches counted beyond old 50 cap");
var all=page.Rows.Concat(catalog.QueryPage("needle",new(Volume:id),256,256).Rows).Concat(catalog.QueryPage("needle",new(Volume:id),512,256).Rows).ToList();
Check(all.Count==625 && all.Select(h=>h.RelativePath).Distinct().Count()==625,"all pages have stable unique rows");
Check(all[^1].Name=="Needle-0624.txt","last result is reachable");
var fileDetails=catalog.GetFileMetadata(id,all[0].RelativePath);
var liveFile=new FileInfo(all[0].FullPath);
Check(fileDetails is { CanReveal:true } && fileDetails.Bytes==liveFile.Length
    && Math.Abs((fileDetails.Created.ToUniversalTime()-liveFile.CreationTimeUtc).TotalMilliseconds)<1
    && Math.Abs((fileDetails.Modified.ToUniversalTime()-liveFile.LastWriteTimeUtc).TotalMilliseconds)<1,
    "file metadata matches size and timestamps on disk");
Check(catalog.QueryPage("",new(Volume:id),0,256,"Projects").Rows.Any(h=>h.Name=="Empty" && h.IsDirectory),"empty directory retained");
Check(catalog.QueryPage("",new(Volume:id),0,256,"Projects\\Empty").Total==0,"empty directory can be browsed");
Check(catalog.QueryPage("CAFÉ (Q4)",new(Volume:id),0,256).Total==1,"accent and punctuation search");
Check(catalog.QueryPage("pr ne",new(Volume:id),0,256).Total==625,"mixed ancestor and filename short prefixes");
Check(catalog.QueryPage("needle",new(Volume:id,Extension:"pdf"),0,256).Total==0,"combined filter excludes other extensions");
var success=catalog.Volumes().Single().Succeeded;
await new Scanner(catalog).ScanAsync(volume,new Progress<ScanProgress>(),new CancellationToken(true));
Check(catalog.Volumes().Single().Succeeded==success && catalog.QueryPage("needle",new(Volume:id),0,256).Total==625,"cancel preserves published catalog");
File.Delete(Path.Combine(root,"Projects","Reports","Needle-0624.txt"));
Directory.Delete(Path.Combine(root,"Projects","Empty"));
await new Scanner(catalog).ScanAsync(volume,new Progress<ScanProgress>(),CancellationToken.None);
Check(catalog.QueryPage("needle",new(Volume:id,IncludeRemoved:false),0,256).Total==624,"hide removed files");
Check(!catalog.QueryPage("",new(Volume:id,IncludeRemoved:false),0,256,"Projects").Rows.Any(h=>h.Name=="Empty"),"hide removed folders");
catalog.Discover([]);
var reopened=new Catalog(data);
Check(reopened.QueryPage("needle",new(Volume:id,Online:false),0,256).Total==625,"offline persistence after reopening catalog");
Check(reopened.QueryPage("needle",new(Volume:id),0,256).Rows.All(h=>!h.CanReveal && (h.Status=="Offline" || h.Status=="Removed")),"offline reveal disabled");
Check(reopened.GetFileMetadata(id,all[0].RelativePath) is { CanReveal:false, Status:"Offline" } saved
    && saved.Bytes==fileDetails!.Bytes && saved.Created==fileDetails.Created,"file metadata remains available offline");
Check(reopened.QueryPage("pr ne",new(Volume:id),0,256).Total==625,"rescan preserves unchanged short-prefix rows");
var removedPath="Projects\\Reports\\Needle-0624.txt";
var missing=reopened.GetFileMetadata(id,removedPath);
Check(missing is {Status:"Removed", Removed:not null, CanReveal:false} && missing.LastSeen <= missing.Removed,
    "confirmed removal keeps metadata and its detection date");
var archive=Path.Combine(data,"test.fiinventory");
reopened.ExportInventory(id,archive);
var imported=reopened.ImportInventory(archive);
Check(imported!=id && reopened.QueryPage("needle",new(Drives:[id,imported]),0,256).Total==1250,
    "checked drives search across local and imported inventories");
Check(reopened.QueryPage("needle",new(Drives:[imported]),0,256).Total==625
    && reopened.QueryPage("needle",new(Drives:[]),0,256).Total==0,"one checked drive and no checked drives");
Check(reopened.GetFileMetadata(imported,removedPath) is {Status:"Removed", CanReveal:false},
    "archived removal is searchable without access to the drive");
Check(reopened.ImportInventory(archive)==imported && reopened.QueryPage("needle",new(Drives:[imported]),0,256).Total==625,
    "repeat import replaces the snapshot without duplicating it");
var invalid=Path.Combine(data,"invalid.fiinventory");
using(var gz=new System.IO.Compression.GZipStream(File.Create(invalid),System.IO.Compression.CompressionLevel.Fastest))
using(var writer=new StreamWriter(gz))
using(var originalGz=new System.IO.Compression.GZipStream(File.OpenRead(archive),System.IO.Compression.CompressionMode.Decompress))
using(var originalReader=new StreamReader(originalGz)) writer.WriteLine(originalReader.ReadLine());
try { reopened.ImportInventory(invalid); throw new Exception("Expected invalid inventory error"); }
catch(InvalidDataException) { Check(reopened.QueryPage("needle",new(Volume:imported),0,256).Total==625,
    "failed import preserves previous snapshot"); }
File.WriteAllText(Path.Combine(root,removedPath),"restored");
reopened.Discover([volume]);
await new Scanner(reopened).ScanAsync(volume,new Progress<ScanProgress>(),CancellationToken.None);
Check(reopened.GetFileMetadata(id,removedPath) is {Status:"Online", Removed:null, CanReveal:true},
    "a returning path is present again");
using(var db=reopened.Open()){using var cmd=Catalog.Command(db,"PRAGMA integrity_check;");Check((string?)cmd.ExecuteScalar()=="ok","SQLite integrity");}
string legacy=Path.Combine(data,"legacy");Directory.CreateDirectory(legacy);
using(var db=new SqliteConnection($"Data Source={Path.Combine(legacy,"catalog.sqlite3")}")){
 db.Open();Catalog.Execute(db,Catalog.Resource("Catalog.sql"));
 Catalog.Execute(db,"ALTER TABLE files DROP COLUMN removed;");
 Catalog.Execute(db,"INSERT INTO volumes(id,mount,label,online) VALUES('old','Z:\\','Old archive',0);");
 Catalog.Execute(db,"INSERT INTO files(volume,rel,name,name_key,parent_key,extension,bytes,modified,created,attributes,seen) VALUES('old','Old\\Nested\\saved.txt','saved.txt','saved.txt','old\\nested','txt',1,1000,1000,0,1000);");
}
var migrated=new Catalog(legacy);
Check(migrated.QueryPage("",new(Volume:"old"),0,256,"").Rows.Single().Name=="Old","legacy catalog root folder inferred");
Check(migrated.QueryPage("",new(Volume:"old"),0,256,"Old").Rows.Single().Name=="Nested","legacy nested folder inferred");
Check(migrated.QueryPage("ne",new(Volume:"old"),0,256).Rows.Any(h=>h.IsDirectory),"legacy directory short prefixes indexed");
var firstScan=ScanDisplay.Create(new(100,0,"",10),TimeSpan.FromSeconds(10));
Check(firstScan.Indeterminate && firstScan.Text.Contains("unknown"),"first scan has honest indeterminate progress");
var repeatScan=ScanDisplay.Create(new(250,0,"",10,1000),TimeSpan.FromSeconds(10));
Check(!repeatScan.Indeterminate && repeatScan.Percent==25 && repeatScan.Text.Contains("remaining"),"repeat scan percentage and ETA");
Check(ScanDisplay.Create(new(1100,0,"",10,1000),TimeSpan.FromSeconds(10)).Indeterminate,"outgrown baseline never reports false completion");
Check(ScanDisplay.Create(new(1000,0,"",10,1000,"Saving catalog"),TimeSpan.FromSeconds(10)).Indeterminate,"saving phase remains visibly active");
Check(ScanDisplay.Create(new(1000,0,"",10,1000,"Complete"),TimeSpan.FromSeconds(10)).Percent==100,"100 percent only after commit");
Check(ScanDisplay.Create(new(100,0,""),TimeSpan.FromSeconds(10),true).Text.Contains("Cancelling"),"cancel state is visible");
try { catalog.QueryPage("needle",new(Volume:id),0,256,cancellationToken:new CancellationToken(true)); throw new Exception("Expected cancellation"); }
catch(OperationCanceledException){Console.WriteLine("PASS cancelled query stops before reading");}
Console.WriteLine("All Explorer catalog, scanner and progress checks passed.");
