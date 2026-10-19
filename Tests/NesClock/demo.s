; Mapper 69, NTSC. Each music update comes from a separate CPU-cycle IRQ.
; No busy burst of four updates at NMI, no DPCM or copyrighted music.
.segment "HEADER"
.byte "NES", $1a, 2, 1, $51, $40, 0,0,0,0,0,0,0,0
.segment "ZEROPAGE"
irq_phase: .res 1
update_count: .res 2
screen_ptr: .res 2
.segment "CODE"
FAMISTUDIO_CFG_EXTERNAL = 1
FAMISTUDIO_CFG_NTSC_SUPPORT = 1
FAMISTUDIO_CFG_PAL_SUPPORT = 0
FAMISTUDIO_CFG_DPCM_SUPPORT = 0
FAMISTUDIO_USE_VOLUME_TRACK = 1
FAMISTUDIO_USE_RELEASE_NOTES = 1
FAMISTUDIO_USE_SLIDE_NOTES = 1
FAMISTUDIO_USE_UPDATE_RATE = 1
.define FAMISTUDIO_CA65_ZP_SEGMENT ZEROPAGE
.define FAMISTUDIO_CA65_RAM_SEGMENT RAM
.define FAMISTUDIO_CA65_CODE_SEGMENT CODE
.include "famistudio_ca65.s"
reset:
    sei
    cld
    ldx #$ff
    txs
    inx
    stx $2000
    stx $2001
    stx $4010
    stx $4015
    lda #$40
    sta $4017
    lda #13
    sta $8000
    lda #0
    sta $a000
    ; Fixed E000 code can safely initialize all switchable PRG/CHR windows.
    ldx #0
@chr:
    stx $8000
    stx $a000
    inx
    cpx #8
    bne @chr
    lda #9
    sta $8000
    lda #0
    sta $a000
    lda #10
    sta $8000
    lda #1
    sta $a000
    lda #11
    sta $8000
    lda #2
    sta $a000
    lda #12
    sta $8000
    lda #0
    sta $a000
    bit $2002
@vblank1:
    bit $2002
    bpl @vblank1
    lda #0
    ldx #0
@clear:
    sta $000,x
    sta $100,x
    sta $200,x
    sta $300,x
    sta $400,x
    sta $500,x
    sta $600,x
    sta $700,x
    inx
    bne @clear
@vblank2:
    bit $2002
    bpl @vblank2
    lda #$3f
    sta $2006
    lda #0
    sta $2006
    ldx #0
@palette:
    lda palette,x
    sta $2007
    inx
    cpx #32
    bne @palette
    lda #$20
    sta $2006
    lda #0
    sta $2006
    lda #<screen
    sta screen_ptr
    lda #>screen
    sta screen_ptr+1
    ldx #4
    ldy #0
@screen:
    lda (screen_ptr),y
    sta $2007
    iny
    bne @screen
    inc screen_ptr+1
    dex
    bne @screen
    lda #0
    sta $2005
    sta $2005
    sta $2000 ; Static screen: NMI stays disabled, no IRQ scheduling interference.
    lda #$0a
    sta $2001
    lda #1
    ldx #<MUSIC_DATA
    ldy #>MUSIC_DATA
    jsr famistudio_init
    lda #0
    jsr famistudio_music_play
    ; Start the counter using the same timer setup as later IRQs.
    jsr reload_timer
    cli
idle:
    jmp idle

irq:
    pha
    txa
    pha
    tya
    pha
    ; Disable + acknowledge before writing the split 16-bit countdown.
    jsr reload_timer
music_call:
    jsr famistudio_update
music_return:
    inc update_count
    bne :+
    inc update_count+1
:
    inc irq_phase
    lda irq_phase
    and #7
    sta irq_phase
    pla
    tay
    pla
    tax
    pla
irq_return:
    rti

reload_timer:
    lda #13
    sta $8000
    lda #0
    sta $a000
    ldx irq_phase
    lda #14
    sta $8000
    lda reload_low,x
    sta $a000
    lda #15
    sta $8000
    lda reload_high,x
    sta $a000
    lda #13
    sta $8000
    lda #$81
    sta $a000
    rts
nmi:
    rti
.align 256
reload_low:
.include "reload-low.inc"
reload_high:
.include "reload-high.inc"
palette:
.repeat 8
.byte $0f, $30, $21, $11
.endrepeat
.segment "MUSIC"
.include "music.s"
screen:
.incbin "screen.bin"
.segment "VECTORS"
.word nmi, reset, irq
.segment "CHARS"
.incbin "demo.chr"
.incbin "demo.chr"
