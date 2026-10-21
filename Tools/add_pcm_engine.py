from pathlib import Path
import re
root=Path(__file__).resolve().parents[1]
eng=root/'SoundEngine'
source=(eng/'famistudio_pcm_ca65.inc').read_text(encoding='utf-8-sig')
(eng/'famistudio_pcm_ca65.inc').write_text(source,encoding='utf-8')
variables='''famistudio_pcm_sample_ptr: .res 128
famistudio_pcm_path: .res 4
famistudio_pcm_current_bank: .res 1
famistudio_pcm_active: .res 4
famistudio_pcm_active_offset: .res 4
famistudio_pcm_pending: .res 4
famistudio_pcm_owns: .res 4
famistudio_pcm_table_ptr: .res 8
famistudio_pcm_stream: .res 2
famistudio_pcm_wait: .res 2
famistudio_pcm_descriptor: .res 2
famistudio_pcm_build_volume: .res 1
famistudio_pcm_build_remainder: .res 1
famistudio_pcm_build_quotient: .res 1
famistudio_pcm_voice: .res 1
'''
def convert(s,kind):
 if kind=='nesasm':
  for label in re.findall(r'(?m)^(famistudio_pcm_\w+):',s):
   if len(label)>31:s=re.sub(r'\b'+label+r'\b',label.replace('famistudio_pcm_','fpc_'),s)
  s=re.sub(r'(?m)^(    (?:asl|lsr|rol|ror))$',r'\1 a',s)
  s=re.sub(r'#>([A-Za-z0-9_]+)',r'#HIGH(\1)',s)
  s=re.sub(r'\((famistudio_pcm_[^)]+)\)',r'[\1]',s)
  s=s.replace('.res ','.rs ')
  s=re.sub(r'(?m)^(\s*(?:lda|ldx|ldy|sta|stx|sty|inc|dec|adc|sbc|cmp|ora|and|eor)\s+)(famistudio_pcm_[\w+]+(?:,x|,y)?)(\s*)$',r'\1<\2\3',s)
  s=re.sub(r'<(famistudio_pcm_[\w+]+,y)',r'\1',s)
  s='\n'.join('    '+l if l.startswith('.') else l for l in s.splitlines())+'\n'
 elif kind=='sdas':
  s=s.replace('.res ','.ds ').replace('.if !FAMISTUDIO_PCM_VOLUME_TABLE_ROM','.ifeq FAMISTUDIO_PCM_VOLUME_TABLE_ROM')
  s=re.sub(r'\$([0-9a-fA-F]+)',r'0x\1',s)
  s=re.sub(r'\((famistudio_pcm_[^)]+)\)',r'[*\1]',s)
  s=re.sub(r'(?m)^(\s*(?:lda|ldx|ldy|sta|stx|sty|inc|dec|adc|sbc|cmp|ora|and|eor)\s+)(famistudio_pcm_[\w+]+(?:,x|,y)?)(\s*)$',r'\1*\2\3',s)
  s=re.sub(r'\*(famistudio_pcm_[\w+]+,y)',r'\1',s)
 elif kind=='asm6':s=s.replace('.res ','.dsb ')
 return s
defaults = """; BEGIN MMC5 PCM DEFINES
.ifndef FAMISTUDIO_USE_APU_PCM
FAMISTUDIO_USE_APU_PCM = 0
.endif
.ifndef FAMISTUDIO_USE_MMC5_PCM
FAMISTUDIO_USE_MMC5_PCM = 0
.endif
.ifndef FAMISTUDIO_USE_VRC6_PCM
FAMISTUDIO_USE_VRC6_PCM = 0
.endif
.ifndef FAMISTUDIO_PCM_VRC6_MASK
FAMISTUDIO_PCM_VRC6_MASK = 3
.endif
.ifndef FAMISTUDIO_USE_PCM
FAMISTUDIO_USE_PCM = FAMISTUDIO_USE_APU_PCM + FAMISTUDIO_USE_MMC5_PCM + FAMISTUDIO_USE_VRC6_PCM
.endif
.ifndef FAMISTUDIO_PCM_VOLUME_TABLE
FAMISTUDIO_PCM_VOLUME_TABLE = $6000
.endif
.ifndef FAMISTUDIO_PCM_VOLUME_TABLE_ROM
FAMISTUDIO_PCM_VOLUME_TABLE_ROM = 0
.endif
.ifndef FAMISTUDIO_PCM_BANK_MASK
FAMISTUDIO_PCM_BANK_MASK = $0f
.endif
.ifndef FAMISTUDIO_PCM_WINDOW
FAMISTUDIO_PCM_WINDOW = $8000
.endif
.ifndef FAMISTUDIO_PCM_TRANSIENT_WINDOW
FAMISTUDIO_PCM_TRANSIENT_WINDOW = 0
.endif
.ifndef FAMISTUDIO_PCM_STOP_IMMEDIATE
FAMISTUDIO_PCM_STOP_IMMEDIATE = 0
.endif
.ifndef FAMISTUDIO_PCM_TRACK_PATH
FAMISTUDIO_PCM_TRACK_PATH = 0
.endif
.if FAMISTUDIO_USE_PCM
FAMISTUDIO_PCM_HEADER_SIZE = 2
.else
FAMISTUDIO_PCM_HEADER_SIZE = 0
.endif
; END MMC5 PCM DEFINES
"""
for name,kind in [('famistudio_ca65.s','ca65'),('famistudio_multi_ca65.s','ca65'),('famistudio_asm6.asm','asm6'),('famistudio_nesasm.asm','nesasm'),('famistudio_sdas.s','sdas')]:
 p=eng/name;s=p.read_text(encoding='utf-8-sig')
 s=re.sub(r'(?m)^\s*\.if FAMISTUDIO_USE_PCM\nFAMISTUDIO_PCM_HEADER_SIZE = 2\n\s*\.else\nFAMISTUDIO_PCM_HEADER_SIZE = 0\n\s*\.endif\n','',s)
 start=s.index('; BEGIN MMC5 PCM IMPLEMENTATION');end=s.index('; END MMC5 PCM IMPLEMENTATION',start)+len('; END MMC5 PCM IMPLEMENTATION')
 s=s[:start]+'; BEGIN MMC5 PCM IMPLEMENTATION\n'+convert(source,kind)+'; END MMC5 PCM IMPLEMENTATION'+s[end:]
 start=s.index('; BEGIN MMC5 PCM DEFINES');end=s.index('; END MMC5 PCM DEFINES',start)+len('; END MMC5 PCM DEFINES')
 s=s[:start]+convert(defaults,kind).strip()+s[end:]
 start=s.index('famistudio_pcm_sample_ptr:');start=s.rfind('.if',0,start)
 end=s.index('famistudio_r0:',start)
 s=s[:start]+convert('.if FAMISTUDIO_USE_PCM\n'+variables+'.endif\n',kind)+s[end:]
 # Generic shared hooks must use a boolean, not the number of destinations.
 impl=s.index('; BEGIN MMC5 PCM IMPLEMENTATION')
 prefix=s[:impl]
 defines_end=prefix.index('; END MMC5 PCM DEFINES')+len('; END MMC5 PCM DEFINES')
 head=prefix[:defines_end];tail=prefix[defines_end:]
 tail=tail.replace('.if FAMISTUDIO_USE_MMC5_PCM','.if FAMISTUDIO_USE_PCM')
 tail=tail.replace('FAMISTUDIO_USE_MMC5_PCM * 2','FAMISTUDIO_PCM_HEADER_SIZE')
 tail=tail.replace('.if FAMISTUDIO_NUM_CHANNELS = 5\n','.if FAMISTUDIO_NUM_CHANNELS + FAMISTUDIO_USE_PCM = 5\n')
 tail=tail.replace('.ifeq FAMISTUDIO_NUM_CHANNELS - 5\n','.ifeq FAMISTUDIO_NUM_CHANNELS - 5 + FAMISTUDIO_USE_PCM\n')
 if '; PCM phase ownership' not in tail:
  label_prefix='@' if kind in ('ca65','asm6') else '.'
  block='; PCM phase ownership: ignore manual resets of a DAC-owned pulse.\n.if FAMISTUDIO_USE_VRC6_PCM\n'
  for n in range(2):
   label=label_prefix+'pcm_phase_'+str(n)
   block+='    lda famistudio_pcm_owns+'+str(n+2)+'\n    beq '+label+'\n    lda famistudio_phase_reset\n    and #'+str(255^(4<<n))+'\n    sta famistudio_phase_reset\n'+label+':\n'
  block+='.endif\n'
  tail=tail.replace('    lda famistudio_phase_reset\n',convert(block,kind)+'    lda famistudio_phase_reset\n',1)
 # The PCM voice owns its physical VRC6 pulse until an explicit yield command.
 if '; PCM ownership: VRC6' not in tail:
  for n in range(2):
   pattern=r'(?m)^(    famistudio_update_channel_sound FAMISTUDIO_VRC6_CH'+str(n)+r'_IDX[^\n]+)$'
   label=('@pcm_skip_vrc6_' if kind in ('ca65','asm6') else '.pcm_skip_vrc6_')+str(n)
   def repl(m):
    return convert('; PCM ownership: VRC6\n.if FAMISTUDIO_USE_VRC6_PCM\n    lda famistudio_pcm_owns+'+str(n+2)+'\n    bne '+label+'\n.endif\n',kind)+m[1]+'\n'+convert('.if FAMISTUDIO_USE_VRC6_PCM\n'+label+':\n.endif\n',kind)
   tail=re.sub(pattern,repl,tail)
 for n in range(2):
  tail=tail.replace('famistudio_pcm_skip_vrc6_'+str(n),('@pcm_skip_vrc6_' if kind in ('ca65','asm6') else '.pcm_skip_vrc6_')+str(n))
 s=head+tail+s[impl:]
 p.write_text(s,encoding='utf-8')
