"""Execute real 6502 cartridge code with cycle-counted mapper-69 IRQs.
Usage: verify.py <build directory> [--py65 <module root>]
No PPU/APU emulation claim: PPU status permits boot, APU bus writes are traced.
Mapper writes occur on the final cycle of each STA instruction; IRQ acceptance
waits for instruction boundaries, including the seven CPU interrupt cycles.
"""
from pathlib import Path
import sys,re,json,hashlib,statistics,argparse
ap=argparse.ArgumentParser();ap.add_argument('output',type=Path);ap.add_argument('--py65',type=Path);args=ap.parse_args()
if args.py65:sys.path.insert(0,str(args.py65))
from py65.devices.mpu6502 import MPU
class Memory:
 def __init__(self,prg):
  self.ram=bytearray(0x800);self.prg=prg;self.banks=[0,1,2,3];self.command=0;self.counter=0;self.enabled=False;self.irq_enabled=False;self.irq=False;self.clock=0;self.pending=[];self.apu={};self.apuwrites=[];self.edges=[]
 def __getitem__(self,a):
  if a<0x2000:return self.ram[a&2047]
  if a==0x2002:return 0x80
  if a>=0x8000:return self.prg[self.banks[(a-0x8000)//8192]*8192+(a&8191)]
  return 0
 def __setitem__(self,a,v):
  if a<0x2000:self.ram[a&2047]=v;return
  self.pending.append((a,v))
 def advance(self,cycles):
  if self.enabled:
   if cycles>self.counter and self.irq_enabled:
    self.irq=True;self.edges.append(self.clock+self.counter+1)
   self.counter=(self.counter-cycles)&65535
  self.clock+=cycles
  for a,v in self.pending:
   if 0x8000<=a<0xa000:self.command=v&15
   elif 0xa000<=a<0xc000:
    if 9<=self.command<=11:self.banks[self.command-9]=v&3
    elif self.command==13:self.enabled=bool(v&128);self.irq_enabled=bool(v&1);self.irq=False
    elif self.command==14:self.counter=(self.counter&0xff00)|v
    elif self.command==15:self.counter=(self.counter&255)|(v<<8)
   elif 0x4000<=a<=0x4017:self.apu[a]=v;self.apuwrites.append((self.clock,a,v))
  self.pending.clear()

def run(rate):
 folder=args.output/f'{rate}x';rom=(folder/f'Clock-{rate}x.nes').read_bytes();assert len(rom)==40976 and (rom[6]>>4)|(rom[7]&240)==69
 labels={m[2]:int(m[1],16) for l in (folder/'demo.lbl').read_text().splitlines() if(m:=re.match(r'al (\w+) \.(.+)',l))}
 mem=Memory(rom[16:16+32768]);cpu=MPU(memory=mem,pc=None)
 times=[];costs=[];irqcost=[];states=[];calls=96*rate*8;inside=False;started=None;instructions=0;pending_at_return=0;invalid_test=False
 while len(states)<calls:
  if mem.irq and not cpu.p&cpu.INTERRUPT:
   before=cpu.processorCycles;cpu.irq();mem.advance(cpu.processorCycles-before);started=mem.clock-7
  if cpu.pc==labels['music_call']:
   times.append(mem.clock);startcall=mem.clock;inside=True
  if cpu.pc==labels['music_return'] and inside:
   costs.append(mem.clock-startcall);states.append(dict(mem.apu));inside=False
   if rate==4 and len(states)==128*rate and not invalid_test:
    snapshot=bytes(mem.ram[:5]+mem.ram[512:]);return_pc=cpu.pc
    cpu.stPushWord((return_pc-1)&65535);cpu.pc=labels['famistudio_music_play'];cpu.a=255
    cpu.processorCycles+=6;mem.advance(6)
    for attempt in range(100):
     before=cpu.processorCycles;cpu.step();mem.advance(cpu.processorCycles-before)
     if cpu.pc==return_pc:break
    else:raise AssertionError('invalid music index did not return')
    assert bytes(mem.ram[:5]+mem.ram[512:])==snapshot,'invalid music index changed song or envelope clock state'
    invalid_test=True
  if cpu.pc==labels['irq_return']:
   irqcost.append(mem.clock+6-started);pending_at_return+=int(mem.irq)
  before=cpu.processorCycles;cpu.step();mem.advance(cpu.processorCycles-before);instructions+=1
  if instructions>40000000:raise RuntimeError('cartridge stalled')
 intervals=[b-a for a,b in zip(times,times[1:])];target=29780.5/rate
 result={'rate':rate,'updates':len(states),'loops':8,'target_interval_cycles':target,'min_interval_cycles':min(intervals),'max_interval_cycles':max(intervals),'mean_interval_cycles':statistics.mean(intervals),'interval_mean_error_cycles':statistics.mean(intervals)-target,'max_interval_error_cycles':max(abs(v-target) for v in intervals),'max_music_update_cycles':max(costs),'mean_music_update_cycles':statistics.mean(costs),'max_irq_total_cycles':max(irqcost),'max_cpu_fraction':max(irqcost)/target,'mean_cpu_fraction':statistics.mean(irqcost)/target,'nested_or_overrun_irq_count':pending_at_return,'sha256':hashlib.sha256(rom).hexdigest(),'instructions':instructions,'invalid_song_index_preserved_state':invalid_test if rate==4 else None}
 (folder/'cycle-verification.json').write_text(json.dumps(result,indent=2)+'\n');(folder/'register-states.json').write_text(json.dumps(states))
 print(json.dumps(result),flush=True)
 assert pending_at_return==0
 assert max(irqcost)<target
 assert result['max_interval_error_cycles']<=4
 assert abs(result['interval_mean_error_cycles'])<.5
 return result,states
all_results=[];basestates=None
for rate in (1,2,4):
 result,states=run(rate);all_results.append(result)
 if rate==1:basestates=states
 else:
  errors=[]
  for t,state in enumerate(states):
   baseline=basestates[t//rate]
   for address in list(range(0x4000,0x4008))+[0x4008,0x400a,0x400b,0x400c,0x400e]:
    if state.get(address)!=baseline.get(address):errors.append((t,hex(address),state.get(address),baseline.get(address)))
  assert not errors,errors[:10]
  result['same_original_phrase_register_states']=True
 (args.output/'cycle-verification.json').write_text(json.dumps({'physical_hardware':False,'emulator':'py65 6502 plus mapper-69 cycle model','scope':'real opcode execution, mapper IRQ and bus register traces; PPU/APU sound require independent console emulator','results':all_results},indent=2)+'\n')
print('PASS all 1/2/4x mapper IRQ timing and preserved phrase checks')
