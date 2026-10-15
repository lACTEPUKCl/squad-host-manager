using System.Diagnostics;
using SquadHostManager.Service;

namespace SquadHostManager.Tests;

public class SteamUpdateTests
{
    [Theory]
    [InlineData("success",0,1,0)]
    [InlineData("denied",0,2,1)]
    [InlineData("network",1,3,0)]
    public async Task CacheUpdateWarmsMetadataAndRecoversOnlyRevokedInstalledManifest(string mode,int exit,int calls,int backups)
    {
        var root=Path.Combine(Path.GetTempPath(),"shm-steam-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var cache=Path.Combine(root,"cache");var bin=Path.Combine(root,"bin");var home=Path.Combine(root,"home");
            Directory.CreateDirectory(bin);Directory.CreateDirectory(Path.Combine(cache,"game","steamapps"));Directory.CreateDirectory(Path.Combine(home,"Steam","logs"));
            File.WriteAllText(Path.Combine(cache,"game","SquadGameServer.sh"),"preserved-game");
            File.WriteAllText(Path.Combine(cache,"game","steamapps","appmanifest_403240.acf"),"old-manifest");
            foreach(var name in new[]{"flock","sleep"})WriteExecutable(Path.Combine(bin,name),"#!/usr/bin/env bash\nexit 0\n");
            WriteExecutable(Path.Combine(bin,"steamcmd.sh"),"""
                #!/usr/bin/env bash
                set -euo pipefail
                printf '%s\n' "$*" >> "$TEST_ROOT/calls"
                [[ "$*" == *'+app_info_print 403240 +app_update 403240'* ]] || exit 99
                log="$HOME/Steam/logs/content_log.txt"
                if [ "$TEST_MODE" = network ]; then echo 'Failed downloading manifests (No connection)' > "$log"; exit 8; fi
                if [ "$TEST_MODE" = denied ] && [ ! -f "$TEST_ROOT/denied-once" ]; then
                  touch "$TEST_ROOT/denied-once"
                  echo "Failed to get manifest request code, 'Access Denied'" > "$log"
                  exit 8
                fi
                if [ "$TEST_MODE" = denied ]; then
                  test ! -e "$TEST_ROOT/cache/game/steamapps/appmanifest_403240.acf"
                  [[ "$*" == *'validate'* ]] || exit 98
                fi
                echo 'new-manifest' > "$TEST_ROOT/cache/game/steamapps/appmanifest_403240.acf"
                """);
            static string BashPath(string value){value=value.Replace('\\','/');return OperatingSystem.IsWindows()?"/"+char.ToLowerInvariant(value[0])+value[2..]:value;}
            static string Shell(string value)=>RemoteDocker.Quote(BashPath(value));
            var bash=OperatingSystem.IsWindows()?Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git","bin","bash.exe"):"/bin/bash";
            var info=new ProcessStartInfo(bash){RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false};
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add($"export PATH={Shell(bin)}:$PATH HOME={Shell(home)} STEAMCMDDIR={Shell(bin)} STEAMAPPID=403240 TEST_ROOT={Shell(root)} TEST_MODE={RemoteDocker.Quote(mode)};\n"+RemoteDocker.CacheScript.Replace("/shm-cache",BashPath(cache)));
            using var process=Process.Start(info)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var detail=await stdout+await stderr;Assert.True(process.ExitCode==exit,detail);
            Assert.Equal(calls,File.ReadAllLines(Path.Combine(root,"calls")).Length);
            var backup=Path.Combine(cache,"metadata-backups");Assert.Equal(backups,Directory.Exists(backup)?Directory.GetFiles(backup).Length:0);
            Assert.Equal(exit==0,File.Exists(Path.Combine(cache,"ready")));
            Assert.Equal("preserved-game",File.ReadAllText(Path.Combine(cache,"game","SquadGameServer.sh")));
            if(mode=="network")Assert.Equal("old-manifest",File.ReadAllText(Path.Combine(cache,"game","steamapps","appmanifest_403240.acf")));
            if(mode=="denied")Assert.Equal("old-manifest",File.ReadAllText(Directory.GetFiles(backup).Single()));
        }
        finally{Directory.Delete(root,true);}
    }
    private static void WriteExecutable(string path,string text)
    {
        File.WriteAllText(path,text.ReplaceLineEndings("\n"));
        if(!OperatingSystem.IsWindows())File.SetUnixFileMode(path,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);
    }
}
