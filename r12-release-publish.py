"""Default: local verification only. Remote publication requires --publish inside GitHub Actions."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import shutil
import subprocess
import urllib.error
import urllib.parse
import urllib.request

REPOSITORY = 'CharizeATOMIC/FamiStudio-Extended'
COMMIT = '33173b20b8cca975aef3269928cb371b12ab76aa'
TAG = 'r12'
API = 'https://api.github.com/repos/' + REPOSITORY

def require(condition, message):
    if not condition: raise RuntimeError(message)

def digest(path):
    h=hashlib.sha256()
    with path.open('rb') as f:
        for block in iter(lambda:f.read(1024*1024), b''): h.update(block)
    return h.hexdigest()

def text_hash(text):
    return hashlib.sha256(text.replace('\r\n','\n').rstrip('\n').encode('utf-8')).hexdigest()

def local_path(root, name):
    require(Path(name).name == name and name not in ['', '.', '..'], 'Unsafe payload filename')
    path=root/name
    require(path.is_file() and not path.is_symlink(), 'Missing or linked payload: '+name)
    return path

def verify_file(path, record):
    require(path.stat().st_size == record['bytes'] and digest(path) == record['sha256'], 'Size/SHA256 mismatch: '+path.name)

def reconstruct(args):
    payload=args.payload.resolve(); repo=args.repository.resolve(); output=args.output.resolve()
    output.mkdir(parents=True, exist_ok=True)
    m=json.loads(local_path(payload,'r12-publish-manifest.json').read_text(encoding='utf-8'))
    require(m['repository']==REPOSITORY and m['target_commit']==COMMIT and m['tag']==TAG, 'Unexpected publication target')
    require(m['prerelease'] is True and m['make_latest'] is False, 'Unexpected release flags')
    require(m['expected_draft_marker']=='untagged-9561c8f1c561cb9ea9d4', 'Unexpected draft identity')
    require(len(m['assets'])==5 and len({a['name'] for a in m['assets']})==5, 'Expected exactly five distinct release assets')
    for kind in ['title','body']:
        p=local_path(payload,m[kind+'_file'])
        require(digest(p)==m[kind+'_file_sha256'], 'Approved '+kind+' bytes changed')
    require(local_path(payload,m['title_file']).read_text(encoding='utf-8').rstrip('\n')==m['title'], 'Title differs')
    require(text_hash(local_path(payload,m['body_file']).read_text(encoding='utf-8'))==m['body_text_sha256'], 'Body differs')
    subprocess.run(['git','cat-file','-e',COMMIT+'^{commit}'],cwd=repo,check=True)
    results=[]
    for a in m['assets']:
        require(Path(a['name']).name==a['name'], 'Invalid asset filename')
        out=output/a['name']
        require(out.resolve().is_relative_to(output), 'Unsafe reconstructed output')
        if a['source']=='chunks':
            with out.open('wb') as dest:
                for part in a['chunks']:
                    source=local_path(payload,part['file']);verify_file(source,part)
                    require(part['bytes']<=20*1024*1024, 'Oversized browser chunk')
                    with source.open('rb') as src:shutil.copyfileobj(src,dest)
        elif a['source']=='git_archive':
            require(a['prefix']=='FamiStudio-Extended/', 'Wrong source ZIP prefix')
            subprocess.run(['git','archive','--format=zip','--prefix='+a['prefix'],'--output='+str(out),COMMIT],cwd=repo,check=True)
        elif a['source']=='existing_repository_file':
            source=args.existing_bundle.resolve() if args.existing_bundle else local_path(repo,a['file'])
            verify_file(source,a);shutil.copyfile(source,out)
        elif a['source']=='file':shutil.copyfile(local_path(payload,a['file']),out)
        else:raise RuntimeError('Unreviewed reconstruction method')
        verify_file(out,a);results.append({'name':a['name'],'bytes':a['bytes'],'sha256':a['sha256']})
    checksum=output/'SHA256SUMS-r12.txt'
    recorded={name:sha for sha,name in (line.split('  ',1) for line in checksum.read_text().splitlines())}
    require(recorded=={a['name']:a['sha256'] for a in m['assets'] if a['name']!='SHA256SUMS-r12.txt'}, 'Checksum list differs from approved assets')
    return m,results

class GitHub:
    def __init__(self):
        self.token=os.environ.get('GH_TOKEN','')
        require(self.token, 'GitHub Actions token is required')
    def request(self, method, path, data=None, upload=False, missing=False):
        url=path if upload else API+path
        if upload:
            parsed=urllib.parse.urlparse(url)
            require(parsed.scheme=='https' and parsed.netloc=='uploads.github.com' and parsed.path.startswith('/repos/'+REPOSITORY+'/releases/'), 'Unexpected upload origin')
        headers={'User-Agent':'FamiStudio-Extended-reviewed-r12-publisher','Accept':'application/vnd.github+json','Authorization':'Bearer '+self.token,'X-GitHub-Api-Version':'2022-11-28'}
        if data is not None:
            if upload: headers['Content-Type']='application/octet-stream'
            else: headers['Content-Type']='application/json';data=json.dumps(data).encode()
        req=urllib.request.Request(url,data=data,headers=headers,method=method)
        try:
            with urllib.request.urlopen(req,timeout=180 if upload else 30) as response:return json.load(response)
        except urllib.error.HTTPError as exc:
            if missing and exc.code==404:return None
            raise RuntimeError('GitHub '+method+' '+urllib.parse.urlparse(url).path+' returned '+str(exc.code)) from None
    def list(self,path):
        result=[]
        for page in range(1,11):
            values=self.request('GET',path+('?page=' if '?' not in path else '&page=')+str(page)+'&per_page=100')
            result.extend(values)
            if len(values)<100:return result
        raise RuntimeError('Unexpected pagination limit')
    def target(self):
        branch=self.request('GET','/git/ref/heads/feature/song-tuning-mixer')
        require(branch['object']['sha']==COMMIT, 'Feature branch moved from approved commit')
        ref=self.request('GET','/git/ref/tags/'+TAG,missing=True)
        if ref is None:return False
        obj=ref['object']
        for _ in range(5):
            if obj['type']=='commit':break
            require(obj['type']=='tag','Unexpected tag object type')
            obj=self.request('GET','/git/tags/'+obj['sha'])['object']
        require(obj['type']=='commit' and obj['sha']==COMMIT, 'Existing r12 tag points to a different commit')
        return True

def metadata_matches(release,m):
    return release['name']==m['title'] and text_hash(release.get('body') or '')==m['body_text_sha256'] and release['prerelease'] is True

def check_assets(api, release, m, complete=False):
    assets=api.list('/releases/'+str(release['id'])+'/assets')
    expected={a['name']:a for a in m['assets']};seen={}
    for a in assets:
        name=a['name'];require(name in expected and name not in seen,'Unexpected or duplicate existing asset: '+name)
        target=expected[name]
        require(a['state']=='uploaded' and a['size']==target['bytes'] and a.get('digest')=='sha256:'+target['sha256'], 'Existing asset is incomplete, differs or lacks a verified digest: '+name)
        seen[name]=a
    if complete:require(set(seen)==set(expected),'Release does not contain all five verified assets')
    return seen

def publish(args,m,verified):
    require(os.environ.get('GITHUB_ACTIONS')=='true' and os.environ.get('GITHUB_REPOSITORY')==REPOSITORY, 'Publication is restricted to this repository in GitHub Actions')
    require(os.environ.get('GITHUB_REF')=='refs/heads/master' and os.environ.get('GITHUB_EVENT_NAME') in ['workflow_dispatch','push'],'Publication requires the reviewed master workflow trigger')
    api=GitHub();api.target()
    releases=api.list('/releases')
    published=[r for r in releases if r['tag_name']==TAG and not r['draft']]
    if published:
        require(len(published)==1 and metadata_matches(published[0],m),'A different published r12 release exists')
        require(api.target(),'Published r12 release has no matching tag')
        check_assets(api,published[0],m,True)
        return {'already_published_unchanged':True,'url':published[0]['html_url'],'assets':verified}
    marker=m['expected_draft_marker']
    candidates=[r for r in releases if r['draft'] and (r['tag_name']==marker or urllib.parse.urlparse(r['html_url']).path.rsplit('/',1)[-1]==marker)]
    require(len(candidates)==1,'The exact observed untagged draft was not uniquely found; no release will be created')
    release=candidates[0]
    require(metadata_matches(release,m),'Draft title, body or prerelease flag differs from approved content')
    require(release['tag_name'] in [TAG,marker],'Draft references an unexpected tag')
    present=check_assets(api,release,m)
    rid=release['id'];print('Verified draft release ID:',rid,flush=True)
    for a in m['assets']:
        if a['name'] in present:print('Skip verified existing asset:',a['name'],flush=True);continue
        current=api.request('GET','/releases/'+str(rid))
        require(current['id']==rid and current['draft'] and current['tag_name']==release['tag_name'] and metadata_matches(current,m),'Draft changed during asset upload')
        upload=current['upload_url'].split('{',1)[0]
        require(urllib.parse.urlparse(upload).path=='/repos/'+REPOSITORY+'/releases/'+str(rid)+'/assets','Upload URL does not belong to this draft')
        api.request('POST',upload+'?'+urllib.parse.urlencode({'name':a['name']}),(args.output/a['name']).read_bytes(),upload=True)
        check_assets(api,current,m)
    current=api.request('GET','/releases/'+str(rid))
    require(current['id']==rid and current['draft'] and current['tag_name']==release['tag_name'] and metadata_matches(current,m),'Draft metadata changed before publication')
    check_assets(api,current,m,True)
    if not api.target():
        # Create only a missing tag. Never update or force an existing ref.
        api.request('POST','/git/refs',{'ref':'refs/tags/'+TAG,'sha':COMMIT})
    require(api.target(),'Exact r12 tag was not created')
    api.request('PATCH','/releases/'+str(rid),{'tag_name':TAG,'target_commitish':COMMIT,'draft':False,'prerelease':True,'make_latest':'false'})
    final=api.request('GET','/releases/'+str(rid))
    require(not final['draft'] and final['tag_name']==TAG and metadata_matches(final,m),'Unexpected published release metadata')
    require(api.target(),'Published tag target differs');check_assets(api,final,m,True)
    return {'published':True,'release_id':rid,'url':final['html_url'],'assets':verified}

def main():
    p=argparse.ArgumentParser();mode=p.add_mutually_exclusive_group()
    mode.add_argument('--verify-only',action='store_true');mode.add_argument('--publish',action='store_true')
    p.add_argument('--repository',type=Path,required=True);p.add_argument('--payload',type=Path,required=True);p.add_argument('--output',type=Path,required=True)
    p.add_argument('--existing-bundle',type=Path)
    args=p.parse_args();args.output=args.output.resolve()
    m,verified=reconstruct(args)
    result=publish(args,m,verified) if args.publish else {'passed':True,'remote_writes_performed':False,'assets':verified}
    (args.output/'verification.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(result,indent=2))

if __name__=='__main__':main()
