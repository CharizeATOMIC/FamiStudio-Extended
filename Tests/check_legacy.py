"""Run the upstream register-trace fixtures against the local build."""
from pathlib import Path
import subprocess,sys
r=Path(__file__).resolve().parents[1];dotnet=Path(sys.argv[1]).resolve()
b=r/'work/legacy-tests';b.mkdir(parents=True,exist_ok=True)
failed=[]
for ref in sorted((r/'FamiStudio/UnitTests').glob('*_FamiStudioRef.txt')):
 name=ref.name.split('_')[0];out=b/(name+'.txt')
 result=subprocess.run([str(dotnet),str(r/'FamiStudio/bin/Release/net8.0/FamiStudio.dll'),str(ref.with_name(name+'.fms')),'unit-test',str(out)],capture_output=True)
 same=result.returncode==0 and out.exists() and out.read_bytes()==ref.read_bytes()
 print('PASS' if same else 'FAIL',name,flush=True)
 if not same:failed.append(name)
assert not failed,failed
