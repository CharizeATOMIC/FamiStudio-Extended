; MMC5 sample cartridge. Normal music is executed by the FamiStudio engine.
; PCM runs independently from scanline IRQs and a bounded blanking-time loop.
; The screen is static during playback: no OAM DMA or PPU upload blocks PCM.
; Song changes stop sound before drawing and restart both clocks together.

FAMISTUDIO_CFG_EXTERNAL                  = 1
FAMISTUDIO_EXP_MMC5                      = 1
.ifndef PCM_DESTINATIONS
PCM_DESTINATIONS = 2
.endif
FAMISTUDIO_USE_APU_PCM = (PCM_DESTINATIONS & 1) <> 0
FAMISTUDIO_USE_MMC5_PCM = (PCM_DESTINATIONS & 2) <> 0
FAMISTUDIO_PCM_BANK_MASK                 = $1f
FAMISTUDIO_PCM_TRANSIENT_WINDOW          = 1
FAMISTUDIO_PCM_STOP_IMMEDIATE            = 1
FAMISTUDIO_PCM_TRACK_PATH                = 1
FAMISTUDIO_CFG_SMOOTH_VIBRATO             = 1
FAMISTUDIO_CFG_DPCM_SUPPORT              = 1
FAMISTUDIO_CFG_EQUALIZER                 = 0
FAMISTUDIO_USE_VOLUME_TRACK              = 1
FAMISTUDIO_USE_VOLUME_SLIDES             = 1
FAMISTUDIO_USE_PITCH_TRACK               = 1
FAMISTUDIO_USE_ENVELOPE_DECAY             = 1
FAMISTUDIO_USE_APU_INSTRUMENT_FLAGS       = 1
FAMISTUDIO_USE_PULSE_SWEEP                = 1
FAMISTUDIO_USE_NOISE_ENVELOPES            = 1
FAMISTUDIO_USE_CUSTOM_TUNING              = 1
FAMISTUDIO_USE_SLIDE_NOTES                = 1
FAMISTUDIO_USE_NOISE_SLIDE_NOTES          = 1
FAMISTUDIO_USE_VIBRATO                    = 1
FAMISTUDIO_USE_ARPEGGIO                   = 1
FAMISTUDIO_USE_DUTYCYCLE_EFFECT           = 1
FAMISTUDIO_USE_DELTA_COUNTER              = 1
FAMISTUDIO_USE_RELEASE_NOTES             = 1
FAMISTUDIO_USE_PHASE_RESET               = 1
FAMISTUDIO_USE_INSTRUMENT_EXTENDED_RANGE  = 1
FAMISTUDIO_USE_DPCM_EXTENDED_RANGE        = 1
FAMISTUDIO_USE_DPCM_BANKSWITCHING         = 1
.ifdef FAMISTUDIO_USE_FAMITRACKER_TEMPO
FAMISTUDIO_USE_FAMITRACKER_DELAYED_NOTES_OR_CUTS = 1
.endif

.define FAMISTUDIO_CA65_ZP_SEGMENT ZEROPAGE
.define FAMISTUDIO_CA65_RAM_SEGMENT RAM
.define FAMISTUDIO_CA65_CODE_SEGMENT CODE
.include "../../SoundEngine/famistudio_ca65.s"

.segment "ZEROPAGE"
frame_count: .res 1
frame_seen: .res 1
song_index: .res 1
song_bank: .res 1
pcm_irq_line: .res 1
blank_samples: .res 1
pcm_phase: .res 1
irq_busy: .res 1
blank_deferred: .res 1
buttons: .res 1
previous_buttons: .res 1
controller_snapshot: .res 1
controller_candidate: .res 1
controller_age: .res 1
controller_bits: .res 1
padding_primary: .res 1
text_ptr: .res 2
song_ptr: .res 2

.macro get_pcm_padding_index
.local combine, neither, finished
.if ::PCM_DESTINATIONS = 3
    ldx famistudio_pcm_path
    lda pcm_path_index,x
    sta padding_primary
    tay
    ldx famistudio_pcm_path+1
    lda pcm_path_index,x
    cpy #16
    bcs combine
    cmp #16
    bcc neither
    ; CMP leaves carry set: add the 76-entry second-active-voice offset.
    adc #75
combine:
    clc
    adc padding_primary
    jmp finished
neither:
    tax
    tya
    asl
    asl
    sta padding_primary
    txa
    clc
    adc padding_primary
.else
  .if ::PCM_DESTINATIONS = 1
    ldx famistudio_pcm_path
  .else
    ldx famistudio_pcm_path+1
  .endif
    lda pcm_path_index,x
.endif
finished:
.endmacro

.segment "HEADER"
.byte 'N','E','S',$1a, 2,1,$51,0, 0,FAMISTUDIO_CFG_PAL_SUPPORT,0,0,0,0,0,0
.segment "SONG"
.res $4000
.segment "DPCM"
.res $2000
.segment "TOC"
max_song: .byte 0
first_dpcm_bank: .byte 0
first_pcm_bank: .byte 0 ; Physical 8 KiB bank, matching this feeder configuration.
.res 5
project_name: .res 28
project_author: .res 28
song_table: .res 48 * 32
.segment "CHR"
.incbin "rom.chr"
.incbin "rom.chr"
.segment "VECTORS"
.word nmi, reset, irq
.segment "CODE"

.proc reset
    sei
    cld
    ldx #$ff
    txs
    inx
    stx $2000
    stx $2001
    stx $4015
    stx $4010
    stx $5204
    lda #$40
    sta $4017
    bit $2002
@wait1:
    bit $2002
    bpl @wait1
    txa
@clear:
    sta $0000,x
    sta $0100,x
    sta $0200,x
    sta $0300,x
    sta $0400,x
    sta $0500,x
    sta $0600,x
    sta $0700,x
    inx
    bne @clear
@wait2:
    bit $2002
    bpl @wait2
    lda #3
    sta $5100 ; Four 8 KiB PRG windows.
    sta $5101 ; Eight 1 KiB CHR windows.
    lda #$44
    sta $5105
    lda #2
    sta $5102
    lda #1
    sta $5103
    lda #0
    sta $5113 ; Work RAM at $6000: PCM gain lookup tables.
    sta $5010 ; PCM write mode, no PCM read IRQ.
    ldx #0
@chr:
    txa
    sta $5120,x
    inx
    cpx #8
    bne @chr
    jsr select_song
main_loop:
    lda frame_count
    cmp frame_seen
    beq main_loop
    sta frame_seen
music_call:
    jsr famistudio_update
music_return:
    jsr poll_buttons
    lda buttons
    eor previous_buttons
    and buttons
    sta previous_buttons
    and #1 ; Right, last bit of the serial controller.
    beq @left
    lda song_index
    cmp max_song
    beq @done
    inc song_index
    jsr select_song
    jmp @done
@left:
    lda previous_buttons
    and #2
    beq @done
    lda song_index
    beq @done
    dec song_index
    jsr select_song
@done:
    lda buttons
    sta previous_buttons
    jmp main_loop
.endproc

.proc select_song
    sei
    lda #0
    sta $5204
    sta $2000
    sta $2001
    sta $4015
    jsr famistudio_pcm_stop
    lda song_index
    ldy #0
    sty song_ptr+1
    asl
    asl
    asl
    rol song_ptr+1
    asl
    rol song_ptr+1
    asl
    rol song_ptr+1
    clc
    adc #<song_table
    sta song_ptr
    lda song_ptr+1
    adc #>song_table
    sta song_ptr+1
    ldy #28
    lda (song_ptr),y
    ora #$80
    sta song_bank
    sta $5114
    clc
    adc #1
    sta $5115
    lda first_dpcm_bank
    ora #$80
    sta $5116
    jsr draw_screen
    ldy #30
    lda (song_ptr),y
    tax
    iny
    lda (song_ptr),y
    tay
.if ::FAMISTUDIO_CFG_PAL_SUPPORT
    lda #0
.else
    lda #1
.endif
    jsr famistudio_init
    lda #0
    jsr famistudio_music_play
    jsr famistudio_update
    bit $2002
@vblank:
    bit $2002
    bpl @vblank
    lda #0
    sta $2005
    sta $2005
    lda #0
    sta pcm_phase
    lda #1
    sta pcm_irq_line
    sta $5203
    bit $5204
    lda #$80
    sta $5204
    sta $2000
    lda #$0a ; Background only: no OAM DMA or sprite fetch dependence.
    sta $2001
    lda frame_count
    sta frame_seen
    cli
    rts
.endproc

.proc irq
    pha
    txa
    pha
    tya
    pha
    lda #1
    sta irq_busy
    bit $5204
    clc
    lda pcm_irq_line
    adc #4
    sta pcm_irq_line
    sta $5203
sample_call:
    jsr famistudio_pcm_service
sample_return:
    ; Publish that the shared feeder is free before testing the pending NMI.
    ; An NMI after this store can safely drain blanking itself. Testing first
    ; left a small lost-wakeup window before the previous busy-clear store.
    lda #0
    sta irq_busy
    lda blank_deferred
    beq complete
    jsr blank_body
complete:
    pla
    tay
    pla
    tax
    pla
    rti
.endproc

 .proc nmi
    pha
    txa
    pha
    tya
    pha
    ; On NTSC phase B the final visible IRQ is at scanline 239. A rare
    ; looping/bank-crossing service can extend into vblank at 241. Never
    ; reenter its shared PCM state: the interrupted IRQ drains blanking.
    lda irq_busy
    beq ordinary
    lda #1
    sta blank_deferred
    bne done
ordinary:
    jsr blank_body
done:
    pla
    tay
    pla
    tax
    pla
    rti
.endproc

.proc blank_body
.if ::FAMISTUDIO_CFG_PAL_SUPPORT
    lda #18 ; 241,245,...309: 78 samples in each PAL frame.
    sta blank_samples
.else
    lda pcm_phase
    bne phase_b
    lda #6 ; 241,245,...261. Frame A has 66 samples.
    sta blank_samples
    jmp first_sample
phase_b:
    lda #5 ; 243,247,...259. Frame B has 65 samples.
    sta blank_samples
    lda blank_deferred
    beq normal_phase_delay
    ; The NMI that deferred us costs exactly 55 cycles. Together with this
    ; dispatch and the IRQ continuation, subtracting 19 five-cycle iterations
    ; reuses the same measured interval as the ordinary blanking path.
    jsr pcm_padding_index
    tax
    lda pcm_padding,x
    cmp #20
    bcc late_service
    sbc #19 ; CMP leaves carry set on this path.
    tay
    lda pcm_padding_remainder,x
    bit buttons
    bit buttons
    jmp pad_sample
late_service:
    ; A second inactive DAC can lengthen the last visible service. If its
    ; remaining delay is exhausted, do not wrap Y and wait 256 iterations.
    ; Service the first blanking sample promptly; report the bounded jitter.
    ldy #1
    lda #0
    jmp pad_sample
normal_phase_delay:
    ; Half of the four-line interval before the phase-B blanking samples.
    ; DPCM steals are bounded separately in the timing proof.
    ldy #45
phase_delay:
    dey
    bne phase_delay
.endif
first_sample:
    ; Only the intervals between samples are padded. The last service returns
    ; promptly so the first visible IRQ of the following frame cannot be lost.
    jsr famistudio_pcm_service_timed
nmi_sample_return:
    dec blank_samples
    beq blank_done
    get_pcm_padding_index
    tax
    ldy pcm_padding,x
    lda pcm_padding_remainder,x
    ; Keep the three-byte DEY/BNE loop within a page, so every iteration
    ; remains five cycles regardless of the linked music-engine size.
    .align 4, $ea
pad_sample:
    dey
    bne pad_sample
    ; 14+A cycles (A=0..4): the taken branch to its next instruction
    ; contributes one cycle; the other two bits contribute two and four.
    lsr
    bcs parity
parity:
    lsr
    bcc skip_two
    bit buttons
skip_two:
    lsr
    bcc skip_four
    bit buttons
    nop
skip_four:
    jmp first_sample
blank_done:
.if ::FAMISTUDIO_CFG_PAL_SUPPORT
    lda #1
.else
    lda pcm_phase
    eor #1
    sta pcm_phase
    asl
    ora #1
.endif
    sta pcm_irq_line
    sta $5203
    lda #0
    sta blank_deferred
    inc frame_count
    rts
.endproc

; A compact index represents one active feeder plus the other inactive path.
; Concurrent active feeders are rejected by the exporter for this 4-line clock.
; Both PCM destinations still retain independent ownership and looping state.
.proc pcm_padding_index
    get_pcm_padding_index
    rts
.endproc

; Generated from every linked feeder path, including indexed page crossings.
; No music or sample data is baked in; page alignment is not required.
pcm_path_index:
.repeat 256
.byte 0
.endrepeat
pcm_padding:
.repeat 176
.byte 1
.endrepeat
pcm_padding_remainder:
.repeat 176
.byte 0
.endrepeat

; Both callbacks preserve X, the feeder's double-buffer state offset.
.proc famistudio_pcm_bank_callback
    clc
    adc first_pcm_bank
    ora #$80
    sta $5114
    rts
.endproc

.proc famistudio_pcm_restore_callback
    lda song_bank
    sta $5114
    rts
.endproc

.proc famistudio_dpcm_bank_callback
    clc
    adc first_dpcm_bank
    ora #$80
    sta $5116
    rts
.endproc

.proc poll_buttons
    ; NTSC DPCM DMA can repeat a controller-port read. Accept two matching
    ; complete reads; bound retries so an unstable controller cannot hold up
    ; the music thread. IRQ/NMI preserve the temporary X/Y registers, but may
    ; stretch the pair beyond the DMC-safe window: this is not a DMA-proof read.
    jsr read_buttons
    sta controller_snapshot
    ldy #3
retry:
    jsr read_buttons
    cmp controller_snapshot
    beq stable
    sta controller_snapshot
    dey
    bne retry
    lda previous_buttons
stable:
    ; DPCM can delete a controller bit. Even two matching serial reads
    ; can both be corrupted into a phantom Right. Require the same state
    ; on three separate music frames before changing the accepted buttons.
    ; This suppresses transient false input; it does not guarantee immunity.
    cmp controller_candidate
    beq confirm
    sta controller_candidate
    lda #0
    sta controller_age
    rts
confirm:
    lda controller_age
    cmp #2
    bcs accepted
    inc controller_age
    lda controller_age
    cmp #2
    bcc done
accepted:
    lda controller_candidate
    sta buttons
done:
    rts
.endproc

.proc read_buttons
    lda #1
    sta $4016
    sta controller_bits
    lda #0
    sta $4016
@read:
    lda $4016
    and #3
    cmp #1
    rol controller_bits
    bcc @read
    lda controller_bits
    rts
.endproc

.proc draw_screen
    bit $2002
    lda #$20
    sta $2006
    lda #0
    sta $2006
    ldy #4
    ldx #0
    lda #$ff
@clear:
    sta $2007
    inx
    bne @clear
    dey
    bne @clear
    lda #$3f
    sta $2006
    lda #0
    sta $2006
    ldx #8
@palette:
    lda #$0f
    sta $2007
    lda #$30
    sta $2007
    lda #$21
    sta $2007
    lda #$11
    sta $2007
    dex
    bne @palette
    lda #<project_name
    sta text_ptr
    lda #>project_name
    sta text_ptr+1
    lda #$20
    ldx #$c2
    jsr draw_text
    lda #<project_author
    sta text_ptr
    lda #>project_author
    sta text_ptr+1
    lda #$21
    ldx #$02
    jsr draw_text
    lda song_ptr
    sta text_ptr
    lda song_ptr+1
    sta text_ptr+1
    lda #$21
    ldx #$82
    jsr draw_text
    lda #<instructions
    sta text_ptr
    lda #>instructions
    sta text_ptr+1
    lda #$22
    ldx #$42
    jmp draw_text
.endproc

.proc draw_text
    sta $2006
    stx $2006
    ldy #0
@loop:
    lda (text_ptr),y
    sta $2007
    iny
    cpy #28
    bne @loop
    rts
.endproc
.include "../../SoundEngine/DemoSource/charmap.inc"
instructions:
.byte "    LEFT / RIGHT: SONG      "
.assert * - instructions = 28, error, "Instruction line width"
