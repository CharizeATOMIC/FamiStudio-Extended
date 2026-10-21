; VRC6 mapper-24 PCM cartridge. Ordinary music runs once per NMI.
; Its mapper IRQ prescaler runs through rendering and blanking at 341/3 CPU cycles.
; The screen is static during playback: no OAM DMA or PPU upload blocks PCM.
; Song changes stop sound before drawing and restart both clocks together.

FAMISTUDIO_CFG_EXTERNAL                  = 1
.ifndef PCM_EXPANSION
PCM_EXPANSION = 1
.endif
FAMISTUDIO_EXP_VRC6 = PCM_EXPANSION
.ifndef PCM_DESTINATIONS
PCM_DESTINATIONS = 4
.endif
FAMISTUDIO_USE_APU_PCM = (PCM_DESTINATIONS & 1) <> 0
FAMISTUDIO_USE_VRC6_PCM = (PCM_DESTINATIONS & 12) <> 0
FAMISTUDIO_PCM_VRC6_MASK = (PCM_DESTINATIONS >> 2) & 3
FAMISTUDIO_PCM_BANK_MASK                 = $3f
FAMISTUDIO_PCM_TRANSIENT_WINDOW          = 1
FAMISTUDIO_PCM_STOP_IMMEDIATE            = 1
FAMISTUDIO_PCM_TRACK_PATH                = 0
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
pcm_bank_base: .res 1
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
text_ptr: .res 2
song_ptr: .res 2

.segment "HEADER"
.byte 'N','E','S',$1a, 2,1,$81,$10, 1,FAMISTUDIO_CFG_PAL_SUPPORT,0,0,0,0,0,0
.segment "SONG"
.res $4000
.segment "DPCM"
.res $2000
.segment "TOC"
max_song: .byte 0
first_dpcm_bank: .byte 0
first_pcm_bank: .byte 0 ; Physical 8 KiB bank; must be even for the 16 KiB window.
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
    stx $f001
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
    lda #$a0
    sta $b003 ; PRG-RAM enabled at $6000, standard vertical CIRAM mapping.
    lda #0
    sta $9003 ; Normal VRC6 audio clock, no oscillator halt.
    sta $d000
    lda #1
    sta $d001
    lda #2
    sta $d002
    lda #3
    sta $d003
    lda #4
    sta $e000
    lda #5
    sta $e001
    lda #6
    sta $e002
    lda #7
    sta $e003
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
    sta $f001
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
    sta song_bank
    sta $8000 ; Song banks are 16 KiB.
    lda first_dpcm_bank
    sta $c000 ; DPCM remains in its independent 8 KiB window.
    lda first_pcm_bank
    lsr
    sta pcm_bank_base
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
    lda #$fc ; Four prescaler counts per PCM tick: 454/455 CPU cycles.
    sta $f000
    lda #3 ; Scanline prescaler, enabled now and after acknowledgement.
    sta $f001
    lda #$80
    sta $2000
    lda #$0a
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
    sta $f002 ; Acknowledge without changing counter or prescaler phase.
sample_call:
    jsr famistudio_pcm_service
sample_return:
    pla
    tay
    pla
    tax
    pla
    rti
.endproc

.proc nmi
    inc frame_count
    rti
.endproc

; Both callbacks preserve X, the feeder's double-buffer state offset.
.proc famistudio_pcm_bank_callback
    clc
    adc pcm_bank_base
    sta $8000
    rts
.endproc
.proc famistudio_pcm_restore_callback
    lda song_bank
    sta $8000
    rts
.endproc
.proc famistudio_dpcm_bank_callback
    clc
    adc first_dpcm_bank
    sta $c000
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
