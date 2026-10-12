using System.Text;

namespace SquadHostManager.Service;
public static class ScopedFiles
{
    public static string Command(string id,string operation,string file="",string? content=null)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(id,"^[a-f0-9]{32}$"))throw new ArgumentException("Invalid instance.");
        return "printf %s "+RemoteDocker.Quote(Convert.ToBase64String(Encoding.UTF8.GetBytes(Script.ReplaceLineEndings("\n"))))+" | base64 -d | python3 - "+RemoteDocker.Quote(id)+" "+RemoteDocker.Quote(operation)+" "+RemoteDocker.Quote(file)+" "+RemoteDocker.Quote(content==null?"":Convert.ToBase64String(Encoding.UTF8.GetBytes(content)));
    }
    public const string Script="""
        import os,sys,ctypes,base64,json,re,stat,tarfile,tempfile
        ident,op,name,content=sys.argv[1:]
        assert re.fullmatch('[a-f0-9]{32}',ident)
        base='/opt/squad-host-manager/instances/'+ident
        root=os.open(base+'/data',os.O_PATH|os.O_DIRECTORY|os.O_NOFOLLOW)
        class How(ctypes.Structure):
            _fields_=[('flags',ctypes.c_uint64),('mode',ctypes.c_uint64),('resolve',ctypes.c_uint64)]
        libc=ctypes.CDLL(None,use_errno=True)
        def opened(fd,path,flags=os.O_RDONLY,mode=0):
            how=How(flags|os.O_CLOEXEC|os.O_NONBLOCK,mode,0x08|0x04)
            result=libc.syscall(437,fd,path.encode(),ctypes.byref(how),ctypes.sizeof(how))
            if result<0:raise OSError(ctypes.get_errno(),'Unsafe or unavailable server path')
            return result
        def bounded(fd,limit):
            with os.fdopen(fd,'rb') as stream:
                if not stat.S_ISREG(os.fstat(stream.fileno()).st_mode):raise ValueError('Not a regular server file')
                data=stream.read(limit+1)
                if len(data)>limit:raise ValueError('File too large')
                return data
        if op=='stage':
            try:fd=opened(root,'.shm-stage')
            except FileNotFoundError:print('');sys.exit(0)
            print(bounded(fd,1024).decode(),end='');sys.exit(0)
        if op=='size':
            pending=['.'];total=0;visited=0
            while pending:
                relative=pending.pop()
                if relative.count('/')>20:continue
                try:directory=opened(root,relative,os.O_RDONLY|os.O_DIRECTORY)
                except OSError:continue
                try:
                    for entry in os.scandir(directory):
                        visited+=1
                        if visited>200000:raise ValueError('Too many server files')
                        info=entry.stat(follow_symlinks=False);path=relative+'/'+entry.name
                        if stat.S_ISDIR(info.st_mode):pending.append(path)
                        elif stat.S_ISREG(info.st_mode):total+=info.st_size
                finally:os.close(directory)
            print(total);sys.exit(0)
        if op in ['read','write']:
            allowed=['Admins.cfg','Bans.cfg','LayerRotation.cfg','LevelRotation.cfg','ExcludedFactions.cfg','ExcludedLayers.cfg','ExcludedLevels.cfg','LayerVoting.cfg','LayerVotingLowPlayers.cfg','LayerVotingNight.cfg','VoteConfig.cfg','MOTD.cfg','ServerMessages.cfg','RemoteAdminListHosts.cfg','RemoteBanListHosts.cfg','CustomOptions.cfg','License.cfg']
            assert name in allowed
            config=os.open(base+'/config',os.O_PATH|os.O_DIRECTORY|os.O_NOFOLLOW)
            if op=='read':
                try:fd=opened(config,name)
                except FileNotFoundError:
                    try:fd=opened(root,'SquadGame/ServerConfig/'+name)
                    except FileNotFoundError:print('');sys.exit(0)
                print(bounded(fd,80000).decode(),end='')
            else:
                data=base64.b64decode(content,validate=True);assert len(data.decode())<=20000 and b'\0' not in data
                target=opened(root,'SquadGame/ServerConfig/'+name,os.O_WRONLY|os.O_CREAT,0o644)
                cached=opened(config,name,os.O_WRONLY|os.O_CREAT,0o644)
                for fd in [target,cached]:
                    with os.fdopen(fd,'wb') as stream:
                        os.ftruncate(stream.fileno(),0);os.fchown(stream.fileno(),1000,1000);stream.write(data);stream.flush();os.fsync(stream.fileno())
                print('Config saved')
            sys.exit(0)
        if op=='download':
            assert name.startswith(('Logs/','OSI_Scoreboards/')) and len(name)<=512 and all(ord(c)>=32 for c in name) and '\\' not in name and not any(p in ['', '.', '..'] for p in name.split('/'))
            print(base64.b64encode(bounded(opened(root,'SquadGame/Saved/'+name),67108864)).decode(),end='');sys.exit(0)
        if op=='restore':
            assert re.fullmatch('/tmp/shm-restore-[a-f0-9]{32}',name)
            parent=os.dup(root)
            for part in ['SquadGame','Saved']:
                try:os.mkdir(part,0o700,dir_fd=parent)
                except FileExistsError:pass
                child=opened(parent,part,os.O_RDONLY|os.O_DIRECTORY);os.fchown(child,1000,1000);os.close(parent);parent=child
            saved=parent
            with os.fdopen(os.open(name,os.O_RDONLY|os.O_NOFOLLOW),'rb') as source:
                with tarfile.open(fileobj=source,mode='r|gz') as archive:
                    total=0;count=0
                    for item in archive:
                        relative=item.name;parts=relative.split('/')
                        assert relative.startswith(('Logs/','OSI_Scoreboards/')) and len(relative)<=512 and all(ord(c)>=32 for c in relative) and '\\' not in relative and all(p not in ['', '.', '..'] for p in parts)
                        total+=item.size;count+=1
                        assert item.isfile() and total<=2147483648 and count<=20000
                        parent=os.dup(saved)
                        try:
                            for part in parts[:-1]:
                                try:os.mkdir(part,0o700,dir_fd=parent)
                                except FileExistsError:pass
                                child=opened(parent,part,os.O_RDONLY|os.O_DIRECTORY);os.fchown(child,1000,1000);os.close(parent);parent=child
                            fd=opened(parent,parts[-1],os.O_WRONLY|os.O_CREAT,0o600)
                            with os.fdopen(fd,'wb') as target:
                                assert stat.S_ISREG(os.fstat(target.fileno()).st_mode);os.ftruncate(target.fileno(),0)
                                os.fchown(target.fileno(),1000,1000)
                                data=archive.extractfile(item);remaining=item.size
                                while remaining:
                                    chunk=data.read(min(65536,remaining));assert chunk;target.write(chunk);remaining-=len(chunk)
                        finally:os.close(parent)
            print('Files restored');sys.exit(0)
        try:saved=opened(root,'SquadGame/Saved',os.O_RDONLY|os.O_DIRECTORY)
        except FileNotFoundError:
            if op=='files':print('[]');sys.exit(0)
            if op=='size':print('0');sys.exit(0)
            raise
        def walk(prefix):
            pending=[prefix];visited=0
            while pending:
                relative=pending.pop()
                if relative.count('/')>16:continue
                try:directory=opened(saved,relative,os.O_RDONLY|os.O_DIRECTORY)
                except OSError:continue
                try:
                    for entry in os.scandir(directory):
                        visited+=1
                        if visited>20000:return
                        path=relative+'/'+entry.name
                        if len(path)>512 or any(ord(c)<32 for c in path):continue
                        info=entry.stat(follow_symlinks=False)
                        if stat.S_ISDIR(info.st_mode):pending.append(path)
                        elif stat.S_ISREG(info.st_mode):yield path,info
                finally:os.close(directory)
        files=(item for prefix in ['Logs','OSI_Scoreboards'] for item in walk(prefix))
        if op=='matchlogs':
            positions=json.loads(base64.b64decode(content));result={};text=[];budget=4194304
            candidates=sorted([(p,i) for p,i in files if p.startswith('Logs/') and p.endswith('.log')],key=lambda item:item[1].st_mtime)[-20:]
            for relative,info in candidates:
                with os.fdopen(opened(saved,relative),'rb') as data:
                    meta=os.fstat(data.fileno());assert stat.S_ISREG(meta.st_mode)
                    old=positions.get(relative,{});offset=old.get('Offset',0) if old.get('Inode')==meta.st_ino else 0
                    if offset<0 or offset>meta.st_size:offset=0
                    data.seek(offset);chunk=data.read(max(0,budget));last=chunk.rfind(b'\n')
                    if last>=0:chunk=chunk[:last+1]
                    text.append(chunk.decode('utf8','replace'));budget-=len(chunk)
                    result[relative]={'Offset':offset+len(chunk),'Inode':meta.st_ino}
            print(json.dumps({'Text':'\n'.join(text),'Positions':result}));sys.exit(0)
        if op=='size':print(sum(info.st_size for path,info in files));sys.exit(0)
        if op=='files':print(json.dumps([{'Path':path,'Bytes':info.st_size} for path,info in files][:10000]));sys.exit(0)
        assert op=='archive'
        descriptor,path=tempfile.mkstemp(prefix='shm-archive.',dir='/tmp');os.close(descriptor)
        class LimitedOutput:
            def __init__(self,target):self.target=target;self.total=0
            def write(self,data):
                self.total+=len(data)
                if name=='backup' and self.total>534773760:raise ValueError('Backup exceeds 510 MiB')
                return self.target.write(data)
            def flush(self):return self.target.flush()
            def tell(self):return self.target.tell()
        try:
            with open(path,'wb') as target,tarfile.open(fileobj=LimitedOutput(target),mode='w:gz',dereference=False) as archive:
                total=0
                for relative,info in files:
                    try:fd=opened(saved,relative)
                    except OSError:continue
                    with os.fdopen(fd,'rb') as data:
                        meta=os.fstat(data.fileno())
                        if not stat.S_ISREG(meta.st_mode):continue
                        total+=meta.st_size
                        if name=='backup' and total>2147483648:raise ValueError('Backup exceeds 2 GiB unpacked')
                        record=tarfile.TarInfo(relative);record.size=meta.st_size;record.mtime=meta.st_mtime;record.mode=0o600
                        archive.addfile(record,data)
            print(path,end='')
        except:
            os.unlink(path);raise
        """;
}
