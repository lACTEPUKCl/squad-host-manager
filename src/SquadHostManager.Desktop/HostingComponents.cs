using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SquadHostManager.Core;

namespace SquadHostManager.Desktop;

public static class HostingComponents
{
    public static async Task<Stream> Open(string architecture,CancellationToken ct=default)
    {
        var asset=architecture switch
        {
            "win-x64"=>"SquadHostManager.Service-win-x64.exe",
            "linux-x64"=>"SquadHostManager.Service-linux-x64",
            "linux-arm64"=>"SquadHostManager.Service-linux-arm64",
            _=>throw new ArgumentException("Unsupported architecture.")
        };
        var resource=typeof(HostingComponents).Assembly.GetManifestResourceStream("SquadHostManager.Coordinator"+(architecture=="win-x64"?"":"."+architecture));
        if(resource!=null)return resource;
        var v=Assembly.GetExecutingAssembly().GetName().Version!;
        var version=new Version(v.Major,v.Minor,v.Build).ToString();
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SquadHostManager","components",version);
        var file=Path.Combine(folder,asset);
        if(File.Exists(file))
        {
            try{await Verify(file,version,asset,ct);return File.OpenRead(file);}
            catch(Exception e) when(e is IOException or InvalidOperationException or JsonException){ }
        }
        var app=Application.Current;
        var completion=new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await app.Dispatcher.InvokeAsync(()=>
        {
            var owner=app.Windows.OfType<MainWindow>().FirstOrDefault(w=>w.IsActive)??app.Windows.OfType<MainWindow>().First();
            if(AppDialog.Show(owner,Localization.T("componentPrompt",architecture),Localization.T("componentTitle"),MessageBoxButton.YesNo,defaultResult:MessageBoxResult.No)!=MessageBoxResult.Yes){completion.TrySetCanceled();return;}
            var window=new Window{Owner=owner,Title=Localization.T("componentTitle"),Width=460,SizeToContent=SizeToContent.Height,WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,WindowStyle=WindowStyle.None,ShowInTaskbar=false};
            var panel=new StackPanel{Margin=new Thickness(24)};var surface=new Border{Child=panel,BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12)};surface.SetResourceReference(Border.BackgroundProperty,"PanelBrush");surface.SetResourceReference(Border.BorderBrushProperty,"LineBrush");window.Content=surface;
            panel.Children.Add(new TextBlock{Text=Localization.T("componentTitle"),FontSize=20,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,16)});
            var bar=new ProgressBar{Height=5,IsIndeterminate=true,Maximum=100,Margin=new Thickness(0,0,0,12)};panel.Children.Add(bar);
            var status=new TextBlock{Text=Localization.T("downloading"),TextWrapping=TextWrapping.Wrap};panel.Children.Add(status);
            var cancel=new Button{Content=Localization.T("dialogCancel"),HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,20,0,0)};panel.Children.Add(cancel);
            var cancellation=CancellationTokenSource.CreateLinkedTokenSource(ct);cancel.Click+=(_,_)=>{cancellation.Cancel();window.Close();};window.Closed+=(_,_)=>cancellation.Cancel();
            window.Loaded+=async(_,_)=>
            {
                try
                {
                    var progress=new Progress<(long Current,long Total)>(value=>{bar.IsIndeterminate=false;bar.Value=value.Current*100d/value.Total;status.Text=Localization.T("downloading")+" · "+(value.Current/1048576d).ToString("0.0")+" / "+(value.Total/1048576d).ToString("0.0")+" MB";});
                    await Download(file,version,asset,progress,cancellation.Token);completion.TrySetResult(file);window.Close();
                }
                catch(OperationCanceledException){completion.TrySetCanceled();window.Close();}
                catch(Exception error){completion.TrySetException(error);window.Close();}
                finally{cancellation.Dispose();}
            };
            window.ShowDialog();
        });
        return File.OpenRead(await completion.Task);
    }
    private static async Task Verify(string file,string version,string asset,CancellationToken ct)
    {
        var manifest=await File.ReadAllBytesAsync(file+".manifest.json",ct);var signature=await File.ReadAllTextAsync(file+".manifest.sig",ct);
        using var stream=File.OpenRead(file);var hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,ct));UpdateTrust.Verify(manifest,signature,version,hash,stream.Length,asset:asset);
    }
    public static async Task Download(string file,string version,string asset,IProgress<(long Current,long Total)>? progress,CancellationToken ct)
    {
        if(!UpdateTrust.AllowedAsset(asset) || !Version.TryParse(version,out _))throw new ArgumentException("Invalid component.");
        using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(15),MaxResponseContentBufferSize=16384};
        var url=$"https://github.com/{Product.Repository}/releases/download/v{version}/{asset}";
        var bytes=await http.GetByteArrayAsync(url+".manifest.json",ct);var signature=await http.GetStringAsync(url+".manifest.sig",ct);
        var manifest=JsonSerializer.Deserialize<UpdateManifest>(bytes)??throw new InvalidOperationException(Localization.T("updateSignatureMissing"));
        UpdateTrust.Verify(bytes,signature,version,manifest.Sha256,manifest.Size,asset:asset);
        if(manifest.Size<=0 || manifest.Size>512L*1024*1024)throw new InvalidOperationException(Localization.T("backupTooLarge"));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);var temp=file+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var source=await http.GetStreamAsync(url,ct))using(var output=File.Create(temp))
            {
                var buffer=new byte[65536];long count=0;int read;
                while((read=await source.ReadAsync(buffer,ct))>0){count+=read;if(count>manifest.Size)throw new InvalidOperationException(Localization.T("updateSignatureMissing"));await output.WriteAsync(buffer.AsMemory(0,read),ct);progress?.Report((count,manifest.Size));}
            }
            using(var check=File.OpenRead(temp)){var hash=Convert.ToHexString(await SHA256.HashDataAsync(check,ct));UpdateTrust.Verify(bytes,signature,version,hash,check.Length,asset:asset);}
            await File.WriteAllBytesAsync(file+".manifest.json",bytes,ct);await File.WriteAllTextAsync(file+".manifest.sig",signature,ct);File.Move(temp,file,true);
        }
        finally{if(File.Exists(temp))File.Delete(temp);}
    }
}
