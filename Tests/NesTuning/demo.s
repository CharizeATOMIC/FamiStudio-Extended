; Minimal NROM-256: the exported music, not the demo program, drives tuning.
.segment "HEADER"
.byte "NES", $1a, 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0
.segment "ZEROPAGE"
frame_ready: .res 1
frame_counter: .res 1
phase: .res 1
screen_ptr: .res 2
.segment "CODE"
FAMISTUDIO_CFG_EXTERNAL = 1
FAMISTUDIO_CFG_NTSC_SUPPORT = 1
FAMISTUDIO_CFG_PAL_SUPPORT = 0
FAMISTUDIO_CFG_DPCM_SUPPORT = 0
FAMISTUDIO_USE_CUSTOM_TUNING = 1
FAMISTUDIO_USE_VOLUME_TRACK = 1
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
    bit $2002
@vblank1:
    bit $2002
    bpl @vblank1
    lda #0
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
    lda #1 ; NTSC
    ldx #<music_data_tuningverification
    ldy #>music_data_tuningverification
    jsr famistudio_init
    lda #0
    jsr famistudio_music_play
    lda #$80
    sta $2000
@frame:
    lda frame_ready
    beq @frame
    lda #0
    sta frame_ready
    ; Highlight the current block, one block = 128 music updates.
    lda $2002
    lda #$21
    sta $2006
    lda #$8f ; row 12, column 15
    sta $2006
    lda phase
    clc
    adc #$35 ; font tile for 1
    sta $2007
    lda #0
    sta $2005
    sta $2005
    lda #$0a
    sta $2001
    jsr famistudio_update
    inc frame_counter
    lda frame_counter
    cmp #128
    bne @frame
    lda #0
    sta frame_counter
    inc phase
    lda phase
    and #3
    sta phase
    jmp @frame
nmi:
    pha
    lda #1
    sta frame_ready
    pla
    rti
irq:
    rti
palette:
.repeat 8
    .byte $0f, $30, $21, $11
.endrepeat
screen:
.incbin "screen.bin"
.include "music.s"
.segment "VECTORS"
.word nmi, reset, irq
.segment "CHARS"
.incbin "demo.chr"
.incbin "demo.chr"
