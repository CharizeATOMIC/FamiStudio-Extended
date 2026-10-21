.ifndef FAMISTUDIO_USE_APU_PCM
FAMISTUDIO_USE_APU_PCM = 0
.endif
.ifndef FAMISTUDIO_USE_MMC5_PCM
FAMISTUDIO_USE_MMC5_PCM = 0
.endif
.ifndef FAMISTUDIO_USE_VRC6_PCM
FAMISTUDIO_USE_VRC6_PCM = 0
.endif
FAMISTUDIO_USE_PCM = FAMISTUDIO_USE_APU_PCM + FAMISTUDIO_USE_MMC5_PCM + FAMISTUDIO_USE_VRC6_PCM
.if ::FAMISTUDIO_USE_PCM
    FAMISTUDIO_PCM_WINDOW = nsf_dpcm_page_start - $2000
.endif

.ifdef FAMISTUDIO

    ; Enable all features.
    FAMISTUDIO_USE_ENVELOPE_DECAY           = 1
    FAMISTUDIO_USE_APU_INSTRUMENT_FLAGS     = 1
FAMISTUDIO_USE_PULSE_SWEEP              = 1
FAMISTUDIO_USE_UPDATE_RATE              = 1
    FAMISTUDIO_USE_NOISE_ENVELOPES           = 1
    FAMISTUDIO_USE_CUSTOM_TUNING             = 1
    FAMISTUDIO_CFG_EXTERNAL                  = 1
    FAMISTUDIO_CFG_SMOOTH_VIBRATO            = 1
    FAMISTUDIO_CFG_DPCM_SUPPORT              = 1
    FAMISTUDIO_USE_VOLUME_TRACK              = 1
    FAMISTUDIO_USE_VOLUME_SLIDES             = 1
    FAMISTUDIO_USE_PITCH_TRACK               = 1
    FAMISTUDIO_USE_SLIDE_NOTES               = 1
    FAMISTUDIO_USE_NOISE_SLIDE_NOTES         = 1
    FAMISTUDIO_USE_VIBRATO                   = 1
    FAMISTUDIO_USE_ARPEGGIO                  = 1
    FAMISTUDIO_USE_DUTYCYCLE_EFFECT          = 1
    FAMISTUDIO_USE_DELTA_COUNTER             = 1
    FAMISTUDIO_USE_RELEASE_NOTES             = 1
    FAMISTUDIO_USE_DPCM_EXTENDED_RANGE       = 1
    FAMISTUDIO_USE_DPCM_BANKSWITCHING        = 1
    FAMISTUDIO_USE_PHASE_RESET               = 1
    FAMISTUDIO_USE_INSTRUMENT_EXTENDED_RANGE = 1
    FAMISTUDIO_USE_FDS_AUTOMOD               = 1

    .define FAMISTUDIO_CA65_ZP_SEGMENT   ZEROPAGE
    .define FAMISTUDIO_CA65_RAM_SEGMENT  RAM
    .define FAMISTUDIO_CA65_CODE_SEGMENT CODE

    .ifdef FAMISTUDIO_CFG_NTSC_SUPPORT
        NSF_NTSC_SUPPORT=1
    .endif
    .ifdef FAMISTUDIO_CFG_PAL_SUPPORT
        NSF_PAL_SUPPORT=1
    .endif
    .ifdef FAMISTUDIO_USE_FAMITRACKER_TEMPO
        FAMISTUDIO_USE_FAMITRACKER_DELAYED_NOTES_OR_CUTS=1
    .endif

    .ifdef FAMISTUDIO_MULTI_EXPANSION
        .include "../../SoundEngine/famistudio_multi_ca65.s"
    .else
        .include "../../SoundEngine/famistudio_ca65.s"
    .endif

.else

    .ifdef FT_NTSC_SUPPORT
        NSF_NTSC_SUPPORT=1
    .endif
    .ifdef FT_PAL_SUPPORT
        NSF_PAL_SUPPORT=1
    .endif
    .include "famitone2.s"
    
.endif

.segment "HEADER"

; NSF header Placeholder so that the debug info addresses matches.
header:   .res 128

.segment "RAM"

.if .defined(NSF_NTSC_SUPPORT) && .defined(NSF_PAL_SUPPORT)
nsf_mode: .res 1
.endif

nsf_song_table_idx: .res 1

.segment "CODE_INIT"

; [in] a = song index.
.proc nsf_init

    ; Each table entry is 4-bytes:
    ;   - start bank (1-byte)
    ;   - start addr in bank (2-byte)
    ;   - flags/unused (1-byte)
    
.if .defined(NSF_NTSC_SUPPORT) && .defined(NSF_PAL_SUPPORT)
    stx nsf_mode
.endif  

    asl
    asl
    tax
    stx nsf_song_table_idx

    ldy nsf_song_table+0, x

    ; Map 0x8000...[driver code] to song data. There may be a DPCM page between
    ; the song data and the driver code. That will get bankswapped when the first
    ; sample plays.
    ldx #0
    @bank_loop:
        tya  
        sta $5ff8, x
        inx
        iny
        cpx nsf_num_song_banks
        bne @bank_loop

    ; Load song data and play
    ldx nsf_song_table_idx
    ldy nsf_song_table+2, x ; hi-byte
    lda nsf_song_table+1, x ; lo-byte
    tax

.if .defined(NSF_NTSC_SUPPORT) && .defined(NSF_PAL_SUPPORT)
    lda nsf_mode
    eor #1
.elseif .defined(NSF_PAL_SUPPORT)
    lda #0 ; PAL
.else
    lda #1 ; NTSC
.endif

.ifdef FAMISTUDIO

    .ifdef FAMISTUDIO_MULTI_EXPANSION
        lda nsf_expansion_mask
        jsr famistudio_multi_init
    .endif

    jsr famistudio_init
    lda #0
    jsr famistudio_music_play
.else
    jsr FamiToneInit
    lda #0
    jsr FamiToneMusicPlay
.endif

    .if ::FAMISTUDIO_USE_PCM
    jmp nsf_pcm_enable
    .else
    rts
    .endif

.endproc

.segment "CODE_PLAY"

.proc nsf_play
.if ::FAMISTUDIO_USE_PCM
    cli
.endif
.ifdef FAMISTUDIO
    jsr famistudio_update
.else
    jsr FamiToneUpdate
.endif
    rts
.endproc

.segment "SONG_DATA"

; Global variables.
nsf_dpcm_page_start: .res 1
nsf_dpcm_page_reg:   .res 1
nsf_expansion_mask:  .res 1
nsf_num_song_banks:  .res 1

; Song table : each entry is 4 bytes
;  - first page of the song (1 byte)
;  - address of the start of the song in page starting at 0x9000 (2 byte)
;  - unused (1-byte)

.if ::FAMISTUDIO_USE_PCM
nsf_pcm_page_start: .res 1
nsf_pcm_page_reg: .res 1
nsf_song_table: .res 250 ; 62 entries and 2 padding bytes
.else
nsf_song_table: .res 252
.endif

.segment "CODE"

.ifdef FAMISTUDIO
.proc famistudio_dpcm_bank_callback
    clc
    adc nsf_dpcm_page_start
    ldx nsf_dpcm_page_reg
    sta $5ff8, x
    rts
.endproc
.endif

.if ::FAMISTUDIO_USE_PCM
.proc famistudio_pcm_bank_callback
    ; Preserve the service's state-slot index in X.
    clc
    adc nsf_pcm_page_start
    ldy nsf_pcm_page_reg
    sta $5ff8,y
    rts
.endproc
.proc famistudio_pcm_restore_callback
    rts
.endproc
.proc nsf_pcm_enable
    sei
    lda #<nsf_pcm_irq
    sta $fffe
    lda #>nsf_pcm_irq
    sta $ffff
.ifdef NSF_PAL_SUPPORT
    lda #159
.else
    lda #191
.endif
    sta $401b
    lda #1
    sta $401c
    lda #1
    sta $401d
    cli
    rts
.endproc
.proc nsf_pcm_irq
    pha
    txa
    pha
    tya
    pha
    lda $401d
    jsr famistudio_pcm_service
    pla
    tay
    pla
    tax
    pla
    rti
.endproc
.endif

.segment "VECTORS"
.if ::FAMISTUDIO_USE_PCM
.word 0,0,nsf_pcm_irq
.else
.res 6
.endif
