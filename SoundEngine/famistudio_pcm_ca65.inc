; Optional independent PCM voices: APU DAC, MMC5 DAC and VRC6 pulse DACs.
; Default-off keeps the ordinary engine unchanged. All voices share one bank
; window and 4 KiB gain table. The host IRQ preserves A/X/Y and acknowledges
; its timer. Bank callbacks preserve X. Never call the music update per sample.
.if FAMISTUDIO_USE_PCM
famistudio_pcm_init:
    php
    sei
    pha
    txa
    pha
    tya
    pha
    lda #0
    sta famistudio_pcm_stream+1
    sta famistudio_pcm_wait
    sta famistudio_pcm_wait+1
    sta famistudio_pcm_voice
    ldx #3
famistudio_pcm_init_voices:
    sta famistudio_pcm_active,x
    sta famistudio_pcm_owns,x
    dex
    bpl famistudio_pcm_init_voices
    ldx #7
famistudio_pcm_init_tables:
    sta famistudio_pcm_table_ptr,x
    dex
    bpl famistudio_pcm_init_tables
    ldx #3
    lda #$ff
famistudio_pcm_init_pending:
    sta famistudio_pcm_pending,x
    dex
    bpl famistudio_pcm_init_pending
    sta famistudio_pcm_current_bank
    lda #0
    sta famistudio_pcm_active_offset
    lda #32
    sta famistudio_pcm_active_offset+1
    lda #64
    sta famistudio_pcm_active_offset+2
    lda #96
    sta famistudio_pcm_active_offset+3
.if FAMISTUDIO_USE_MMC5_PCM
    lda #0
    sta $5010
    lda #$80
    sta $5011
.endif
    lda #>FAMISTUDIO_PCM_VOLUME_TABLE
    sta famistudio_pcm_table_ptr+1
.if !FAMISTUDIO_PCM_VOLUME_TABLE_ROM
    lda #0
    sta famistudio_pcm_build_volume
famistudio_pcm_table_volume:
    lda #0
    sta famistudio_pcm_build_remainder
    sta famistudio_pcm_build_quotient
    ldy #0
    lda #128
    sta (famistudio_pcm_table_ptr),y
    ldy #128
    sta (famistudio_pcm_table_ptr),y
    ldx #1
famistudio_pcm_table_delta:
    lda famistudio_pcm_build_remainder
    clc
    adc famistudio_pcm_build_volume
    cmp #15
    bcc famistudio_pcm_table_no_divide
    sbc #15
    inc famistudio_pcm_build_quotient
famistudio_pcm_table_no_divide:
    sta famistudio_pcm_build_remainder
    txa
    ora #128
    tay
    lda famistudio_pcm_build_quotient
    ora #128
    sta (famistudio_pcm_table_ptr),y
    lda #128
    sec
    sbc famistudio_pcm_build_quotient
    pha
    txa
    eor #$ff
    clc
    adc #129
    tay
    pla
    sta (famistudio_pcm_table_ptr),y
    inx
    bpl famistudio_pcm_table_delta
    inc famistudio_pcm_table_ptr+1
    inc famistudio_pcm_build_volume
    lda famistudio_pcm_build_volume
    cmp #16
    bne famistudio_pcm_table_volume
.endif
    lda #>FAMISTUDIO_PCM_VOLUME_TABLE
    sta famistudio_pcm_table_ptr+1
    lda #>FAMISTUDIO_PCM_VOLUME_TABLE
    sta famistudio_pcm_table_ptr+3
    sta famistudio_pcm_table_ptr+5
    sta famistudio_pcm_table_ptr+7
    pla
    tay
    pla
    tax
    pla
    plp
    rts

famistudio_pcm_stop:
    php
    sei
    lda #0
    sta famistudio_pcm_stream+1
    sta famistudio_pcm_wait
    sta famistudio_pcm_wait+1
    ldx #3
famistudio_pcm_stop_voices:
    sta famistudio_pcm_active,x
    dex
    bpl famistudio_pcm_stop_voices
.if FAMISTUDIO_USE_APU_PCM
    lda famistudio_pcm_owns
    beq famistudio_pcm_stop_not_apu
    lda #64
    sta $4011
famistudio_pcm_stop_not_apu:
.endif
.if FAMISTUDIO_USE_MMC5_PCM
    lda famistudio_pcm_owns+1
    beq famistudio_pcm_stop_not_mmc5
    lda #128
    sta $5011
famistudio_pcm_stop_not_mmc5:
.endif
.if FAMISTUDIO_USE_VRC6_PCM
    lda famistudio_pcm_owns+2
    beq famistudio_pcm_stop_not_vrc6_1
    lda #$88
    sta FAMISTUDIO_VRC6_PL1_VOL
famistudio_pcm_stop_not_vrc6_1:
    lda famistudio_pcm_owns+3
    beq famistudio_pcm_stop_not_vrc6_2
    lda #$88
    sta FAMISTUDIO_VRC6_PL2_VOL
famistudio_pcm_stop_not_vrc6_2:
.endif
    lda #0
    ldx #3
famistudio_pcm_stop_clear_owners:
    sta famistudio_pcm_owns,x
    dex
    bpl famistudio_pcm_stop_clear_owners
    lda #$ff
    ldx #3
famistudio_pcm_stop_clear_pending:
    sta famistudio_pcm_pending,x
    dex
    bpl famistudio_pcm_stop_clear_pending
    plp
    rts

; Read sparse control bytes using private pointers. IRQs never touch them.
famistudio_pcm_read:
    ldy #0
    lda (famistudio_pcm_stream),y
    inc famistudio_pcm_stream
    bne famistudio_pcm_read_done
    inc famistudio_pcm_stream+1
famistudio_pcm_read_done:
    rts

famistudio_pcm_update:
    lda famistudio_pcm_stream+1
    bne famistudio_pcm_update_enabled
    rts
famistudio_pcm_update_enabled:
    lda famistudio_pcm_wait
    ora famistudio_pcm_wait+1
    beq famistudio_pcm_command
    lda famistudio_pcm_wait
    bne famistudio_pcm_wait_low
    dec famistudio_pcm_wait+1
famistudio_pcm_wait_low:
    dec famistudio_pcm_wait
    lda famistudio_pcm_wait
    ora famistudio_pcm_wait+1
    beq famistudio_pcm_command
    rts
famistudio_pcm_command:
    jsr famistudio_pcm_read
    cmp #$10
    bcc famistudio_pcm_control
    and #15
    clc
    adc #>FAMISTUDIO_PCM_VOLUME_TABLE
    pha
    lda famistudio_pcm_voice
    asl
    tay
    pla
    sta famistudio_pcm_table_ptr+1,y
    jmp famistudio_pcm_command
famistudio_pcm_control:
    cmp #0
    bne famistudio_pcm_not_wait
    jsr famistudio_pcm_read
    sta famistudio_pcm_wait
    jsr famistudio_pcm_read
    sta famistudio_pcm_wait+1
    rts
famistudio_pcm_not_wait:
    cmp #1
    beq famistudio_pcm_trigger
    jmp famistudio_pcm_not_trigger
famistudio_pcm_trigger:
    jsr famistudio_pcm_read
    sta famistudio_pcm_descriptor
    jsr famistudio_pcm_read
    sta famistudio_pcm_descriptor+1
    ; Publish only after the inactive slot is completely populated.
    php
    sei
    ldy famistudio_pcm_voice
    lda #$ff
    sta famistudio_pcm_pending,y
    lda famistudio_pcm_active_offset,y
    eor #16
    tax
    plp
    ldy #0
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+0,x
    sta famistudio_pcm_sample_ptr+6,x
    iny
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+1,x
    sta famistudio_pcm_sample_ptr+7,x
    iny
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+2,x
    sta famistudio_pcm_sample_ptr+8,x
    iny
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+3,x
    sta famistudio_pcm_sample_ptr+9,x
    iny
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+4,x
    sta famistudio_pcm_sample_ptr+10,x
    iny
    lda (famistudio_pcm_descriptor),y
    sta famistudio_pcm_sample_ptr+5,x
    ; Slots contain immutable ROM sample data. Cache a one-byte loop after
    ; its first IRQ read; gain is still looked up anew on every sample tick.
    ; Both fields are initialized before publishing this inactive slot.
    lda #0
    sta famistudio_pcm_sample_ptr+11,x
    lda #1
    sta famistudio_pcm_sample_ptr+12,x
    lda famistudio_pcm_sample_ptr+3,x
    bne famistudio_pcm_trigger_cache_ready
    lda famistudio_pcm_sample_ptr+2,x
    cmp #1
    bne famistudio_pcm_trigger_cache_ready
    lda famistudio_pcm_sample_ptr+5,x
    beq famistudio_pcm_trigger_cache_ready
    lda #$81
    sta famistudio_pcm_sample_ptr+12,x
famistudio_pcm_trigger_cache_ready:
    ldy famistudio_pcm_voice
.if FAMISTUDIO_USE_APU_PCM
    cpy #0
    bne famistudio_pcm_trigger_not_apu
    lda #$0f
    sta $4015
famistudio_pcm_trigger_not_apu:
.endif
.if FAMISTUDIO_USE_VRC6_PCM
    cpy #2
    bne famistudio_pcm_trigger_not_vrc6_1
    lda #$80
    sta FAMISTUDIO_VRC6_PL1_HI
famistudio_pcm_trigger_not_vrc6_1:
    cpy #3
    bne famistudio_pcm_trigger_not_vrc6_2
    lda #$80
    sta FAMISTUDIO_VRC6_PL2_HI
famistudio_pcm_trigger_not_vrc6_2:
.endif
    lda #1
    sta famistudio_pcm_owns,y
    txa
    sta famistudio_pcm_pending,y
    jmp famistudio_pcm_command
famistudio_pcm_not_trigger:
    cmp #2
    bne famistudio_pcm_not_stop
    ldy famistudio_pcm_voice
.if FAMISTUDIO_PCM_STOP_IMMEDIATE
    ; A scanline-timed host can complete the stop before publishing another
    ; destination, avoiding two heavy pending operations in one interrupt.
    php
    sei
    lda #0
    sta famistudio_pcm_active,y
    lda #$ff
    sta famistudio_pcm_pending,y
    lda famistudio_pcm_owns,y
    beq famistudio_pcm_immediate_done
.if FAMISTUDIO_USE_APU_PCM
    cpy #0
    bne famistudio_pcm_immediate_not_apu
    lda #64
    sta $4011
famistudio_pcm_immediate_not_apu:
.endif
.if FAMISTUDIO_USE_MMC5_PCM
    cpy #1
    bne famistudio_pcm_immediate_not_mmc5
    lda #128
    sta $5011
famistudio_pcm_immediate_not_mmc5:
.endif
.if FAMISTUDIO_USE_VRC6_PCM
    cpy #2
    bne famistudio_pcm_immediate_not_vrc6_1
    lda #$88
    sta FAMISTUDIO_VRC6_PL1_VOL
famistudio_pcm_immediate_not_vrc6_1:
    cpy #3
    bne famistudio_pcm_immediate_not_vrc6_2
    lda #$88
    sta FAMISTUDIO_VRC6_PL2_VOL
famistudio_pcm_immediate_not_vrc6_2:
.endif
famistudio_pcm_immediate_done:
    plp
.else
    lda #$80
    sta famistudio_pcm_pending,y
.endif
    jmp famistudio_pcm_command
famistudio_pcm_not_stop:
    cmp #3
    bne famistudio_pcm_not_jump
    jsr famistudio_pcm_read
    pha
    jsr famistudio_pcm_read
    sta famistudio_pcm_stream+1
    pla
    sta famistudio_pcm_stream
    jmp famistudio_pcm_command
famistudio_pcm_not_jump:
    cmp #5
    bne famistudio_pcm_not_select
    jsr famistudio_pcm_read
    and #3
    sta famistudio_pcm_voice
    jmp famistudio_pcm_command
famistudio_pcm_not_select:
    cmp #6
    bne famistudio_pcm_end
    ; Hand the original DPCM/pulse voice back without a later IRQ writing
    ; a neutral DAC value over the ordinary engine's new register state.
    php
    sei
    ldy famistudio_pcm_voice
    lda #$ff
    sta famistudio_pcm_pending,y
    lda #0
    sta famistudio_pcm_active,y
    sta famistudio_pcm_owns,y
.if FAMISTUDIO_USE_VRC6_PCM
    lda #$80
    cpy #2
    bne famistudio_pcm_yield_not_vrc6_1
    sta famistudio_vrc6_pulse1_prev_hi
famistudio_pcm_yield_not_vrc6_1:
    cpy #3
    bne famistudio_pcm_yield_not_vrc6_2
    sta famistudio_vrc6_pulse2_prev_hi
famistudio_pcm_yield_not_vrc6_2:
.endif
    plp
    jmp famistudio_pcm_command
famistudio_pcm_end:
    lda #0
    sta famistudio_pcm_stream+1
    rts

famistudio_pcm_service_timed:
famistudio_pcm_service:
.if FAMISTUDIO_USE_APU_PCM
    jsr famistudio_pcm_service_apu
.endif
.if FAMISTUDIO_USE_MMC5_PCM
    jsr famistudio_pcm_service_mmc5
.endif
.if FAMISTUDIO_USE_VRC6_PCM * (FAMISTUDIO_PCM_VRC6_MASK & 1)
    jsr famistudio_pcm_service_vrc6_1
.endif
.if FAMISTUDIO_USE_VRC6_PCM * (FAMISTUDIO_PCM_VRC6_MASK & 2)
    jsr famistudio_pcm_service_vrc6_2
.endif
    rts

; Read once when accepting a descriptor. Ordinary streaming reads do not
; update the cache. Bank callbacks and the sample view match the normal path.
famistudio_pcm_read_initial:
.if FAMISTUDIO_PCM_TRANSIENT_WINDOW
    lda famistudio_pcm_sample_ptr+4,x
    jsr famistudio_pcm_bank_callback
    lda (famistudio_pcm_sample_ptr,x)
    pha
    jsr famistudio_pcm_restore_callback
    pla
.else
    lda famistudio_pcm_sample_ptr+4,x
    cmp famistudio_pcm_current_bank
    beq famistudio_pcm_initial_bank_ready
    sta famistudio_pcm_current_bank
    jsr famistudio_pcm_bank_callback
famistudio_pcm_initial_bank_ready:
    lda (famistudio_pcm_sample_ptr,x)
.endif
    sta famistudio_pcm_sample_ptr+11,x
    rts

.if FAMISTUDIO_USE_APU_PCM
famistudio_pcm_service_apu:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #$ff
    sta famistudio_pcm_path+0
.endif
    lda famistudio_pcm_owns+0
    bne famistudio_pcm_owned_apu
    rts
famistudio_pcm_owned_apu:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #0
    sta famistudio_pcm_path+0
.endif
    lda famistudio_song_speed
    bpl famistudio_pcm_service_running_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #128
    sta famistudio_pcm_path+0
.endif
    lda #$80
    lsr
    sta $4011
    rts
famistudio_pcm_service_running_apu:
    lda famistudio_pcm_pending+0
    cmp #$ff
    beq famistudio_pcm_no_command_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    inc famistudio_pcm_path+0
.endif
    cmp #$80
    bne famistudio_pcm_not_disable_apu_1
    jmp famistudio_pcm_disable_apu
famistudio_pcm_not_disable_apu_1:
    sta famistudio_pcm_active_offset+0
    tax
    lda famistudio_pcm_sample_ptr+12,x
    sta famistudio_pcm_active+0
    lda #$ff
    sta famistudio_pcm_pending+0
    ; Newly published descriptors always have a positive length. Even a
    ; constant loop must read its new source byte before using the cache.
    jsr famistudio_pcm_read_initial
    jmp famistudio_pcm_output_sample_apu
famistudio_pcm_no_command_apu:
    lda famistudio_pcm_active+0
    bne famistudio_pcm_not_idle_apu
    jmp famistudio_pcm_idle_apu
famistudio_pcm_not_idle_apu:
    ldx famistudio_pcm_active_offset+0
    lda famistudio_pcm_sample_ptr+2,x
    ora famistudio_pcm_sample_ptr+3,x
    bne famistudio_pcm_read_sample_apu
    lda famistudio_pcm_active+0
    bpl famistudio_pcm_normal_end_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #64
    sta famistudio_pcm_path+0
.endif
    lda famistudio_pcm_sample_ptr+11,x
    tay
    lda (famistudio_pcm_table_ptr+0),y
    lsr
    sta $4011
    rts
famistudio_pcm_normal_end_apu:
    lda famistudio_pcm_sample_ptr+5,x
    bne famistudio_pcm_not_disable_apu_2
    jmp famistudio_pcm_disable_apu
famistudio_pcm_not_disable_apu_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #2
    sta famistudio_pcm_path+0
.endif
    lda famistudio_pcm_sample_ptr+6,x
    sta famistudio_pcm_sample_ptr+0,x
    lda famistudio_pcm_sample_ptr+7,x
    sta famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+8,x
    sta famistudio_pcm_sample_ptr+2,x
    lda famistudio_pcm_sample_ptr+9,x
    sta famistudio_pcm_sample_ptr+3,x
    lda famistudio_pcm_sample_ptr+10,x
    sta famistudio_pcm_sample_ptr+4,x
famistudio_pcm_read_sample_apu:
.if FAMISTUDIO_PCM_TRANSIENT_WINDOW
    lda famistudio_pcm_sample_ptr+4,x
    jsr famistudio_pcm_bank_callback
    lda (famistudio_pcm_sample_ptr,x)
    pha
    jsr famistudio_pcm_restore_callback
    pla
.else
    lda famistudio_pcm_sample_ptr+4,x
    cmp famistudio_pcm_current_bank
    beq famistudio_pcm_bank_ready_apu
    sta famistudio_pcm_current_bank
    jsr famistudio_pcm_bank_callback
famistudio_pcm_bank_ready_apu:
    lda (famistudio_pcm_sample_ptr,x)
.endif
famistudio_pcm_output_sample_apu:
    tay
    lda (famistudio_pcm_table_ptr+0),y
    lsr
    sta $4011
    inc famistudio_pcm_sample_ptr+0,x
    bne famistudio_pcm_pointer_ready_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #4
    sta famistudio_pcm_path+0
.endif
    inc famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+1,x
    and #FAMISTUDIO_PCM_BANK_MASK
    bne famistudio_pcm_pointer_ready_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #8
    sta famistudio_pcm_path+0
.endif
    lda #>FAMISTUDIO_PCM_WINDOW
    sta famistudio_pcm_sample_ptr+1,x
    inc famistudio_pcm_sample_ptr+4,x
famistudio_pcm_pointer_ready_apu:
    lda famistudio_pcm_sample_ptr+2,x
    bne famistudio_pcm_count_low_apu
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #16
    sta famistudio_pcm_path+0
.endif
    dec famistudio_pcm_sample_ptr+3,x
famistudio_pcm_count_low_apu:
    dec famistudio_pcm_sample_ptr+2,x
    rts
famistudio_pcm_disable_apu:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #64
    sta famistudio_pcm_path+0
.endif
    lda #0
    sta famistudio_pcm_active+0
    lda #$ff
    sta famistudio_pcm_pending+0
famistudio_pcm_silence_apu:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+0
    ora #32
    sta famistudio_pcm_path+0
.endif
    lda #$80
    lsr
    sta $4011
    rts
famistudio_pcm_idle_apu:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #32
    sta famistudio_pcm_path+0
.endif
    rts
.endif

.if FAMISTUDIO_USE_MMC5_PCM
famistudio_pcm_service_mmc5:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #$ff
    sta famistudio_pcm_path+1
.endif
    lda famistudio_pcm_owns+1
    bne famistudio_pcm_owned_mmc5
    rts
famistudio_pcm_owned_mmc5:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #0
    sta famistudio_pcm_path+1
.endif
    lda famistudio_song_speed
    bpl famistudio_pcm_service_running_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #128
    sta famistudio_pcm_path+1
.endif
    lda #$80
    sta $5011
    rts
famistudio_pcm_service_running_mmc5:
    lda famistudio_pcm_pending+1
    cmp #$ff
    beq famistudio_pcm_no_command_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    inc famistudio_pcm_path+1
.endif
    cmp #$80
    bne famistudio_pcm_not_disable_mmc5_1
    jmp famistudio_pcm_disable_mmc5
famistudio_pcm_not_disable_mmc5_1:
    sta famistudio_pcm_active_offset+1
    tax
    lda famistudio_pcm_sample_ptr+12,x
    sta famistudio_pcm_active+1
    lda #$ff
    sta famistudio_pcm_pending+1
    ; Newly published descriptors always have a positive length. Even a
    ; constant loop must read its new source byte before using the cache.
    jsr famistudio_pcm_read_initial
    jmp famistudio_pcm_output_sample_mmc5
famistudio_pcm_no_command_mmc5:
    lda famistudio_pcm_active+1
    bne famistudio_pcm_not_idle_mmc5
    jmp famistudio_pcm_idle_mmc5
famistudio_pcm_not_idle_mmc5:
    ldx famistudio_pcm_active_offset+1
    lda famistudio_pcm_sample_ptr+2,x
    ora famistudio_pcm_sample_ptr+3,x
    bne famistudio_pcm_read_sample_mmc5
    lda famistudio_pcm_active+1
    bpl famistudio_pcm_normal_end_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #64
    sta famistudio_pcm_path+1
.endif
    lda famistudio_pcm_sample_ptr+11,x
    tay
    lda (famistudio_pcm_table_ptr+2),y
    sta $5011
    rts
famistudio_pcm_normal_end_mmc5:
    lda famistudio_pcm_sample_ptr+5,x
    bne famistudio_pcm_not_disable_mmc5_2
    jmp famistudio_pcm_disable_mmc5
famistudio_pcm_not_disable_mmc5_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #2
    sta famistudio_pcm_path+1
.endif
    lda famistudio_pcm_sample_ptr+6,x
    sta famistudio_pcm_sample_ptr+0,x
    lda famistudio_pcm_sample_ptr+7,x
    sta famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+8,x
    sta famistudio_pcm_sample_ptr+2,x
    lda famistudio_pcm_sample_ptr+9,x
    sta famistudio_pcm_sample_ptr+3,x
    lda famistudio_pcm_sample_ptr+10,x
    sta famistudio_pcm_sample_ptr+4,x
famistudio_pcm_read_sample_mmc5:
.if FAMISTUDIO_PCM_TRANSIENT_WINDOW
    lda famistudio_pcm_sample_ptr+4,x
    jsr famistudio_pcm_bank_callback
    lda (famistudio_pcm_sample_ptr,x)
    pha
    jsr famistudio_pcm_restore_callback
    pla
.else
    lda famistudio_pcm_sample_ptr+4,x
    cmp famistudio_pcm_current_bank
    beq famistudio_pcm_bank_ready_mmc5
    sta famistudio_pcm_current_bank
    jsr famistudio_pcm_bank_callback
famistudio_pcm_bank_ready_mmc5:
    lda (famistudio_pcm_sample_ptr,x)
.endif
famistudio_pcm_output_sample_mmc5:
    tay
    lda (famistudio_pcm_table_ptr+2),y
    sta $5011
    inc famistudio_pcm_sample_ptr+0,x
    bne famistudio_pcm_pointer_ready_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #4
    sta famistudio_pcm_path+1
.endif
    inc famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+1,x
    and #FAMISTUDIO_PCM_BANK_MASK
    bne famistudio_pcm_pointer_ready_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #8
    sta famistudio_pcm_path+1
.endif
    lda #>FAMISTUDIO_PCM_WINDOW
    sta famistudio_pcm_sample_ptr+1,x
    inc famistudio_pcm_sample_ptr+4,x
famistudio_pcm_pointer_ready_mmc5:
    lda famistudio_pcm_sample_ptr+2,x
    bne famistudio_pcm_count_low_mmc5
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #16
    sta famistudio_pcm_path+1
.endif
    dec famistudio_pcm_sample_ptr+3,x
famistudio_pcm_count_low_mmc5:
    dec famistudio_pcm_sample_ptr+2,x
    rts
famistudio_pcm_disable_mmc5:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #64
    sta famistudio_pcm_path+1
.endif
    lda #0
    sta famistudio_pcm_active+1
    lda #$ff
    sta famistudio_pcm_pending+1
famistudio_pcm_silence_mmc5:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+1
    ora #32
    sta famistudio_pcm_path+1
.endif
    lda #$80
    sta $5011
    rts
famistudio_pcm_idle_mmc5:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #32
    sta famistudio_pcm_path+1
.endif
    rts
.endif

.if FAMISTUDIO_USE_VRC6_PCM * (FAMISTUDIO_PCM_VRC6_MASK & 1)
famistudio_pcm_service_vrc6_1:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #$ff
    sta famistudio_pcm_path+2
.endif
    lda famistudio_pcm_owns+2
    bne famistudio_pcm_owned_vrc6_1
    rts
famistudio_pcm_owned_vrc6_1:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #0
    sta famistudio_pcm_path+2
.endif
    lda famistudio_song_speed
    bpl famistudio_pcm_service_running_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #128
    sta famistudio_pcm_path+2
.endif
    lda #$80
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL1_VOL
    rts
famistudio_pcm_service_running_vrc6_1:
    lda famistudio_pcm_pending+2
    cmp #$ff
    beq famistudio_pcm_no_command_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    inc famistudio_pcm_path+2
.endif
    cmp #$80
    bne famistudio_pcm_not_disable_vrc6_1_1
    jmp famistudio_pcm_disable_vrc6_1
famistudio_pcm_not_disable_vrc6_1_1:
    sta famistudio_pcm_active_offset+2
    tax
    lda famistudio_pcm_sample_ptr+12,x
    sta famistudio_pcm_active+2
    lda #$ff
    sta famistudio_pcm_pending+2
    ; Newly published descriptors always have a positive length. Even a
    ; constant loop must read its new source byte before using the cache.
    jsr famistudio_pcm_read_initial
    jmp famistudio_pcm_output_sample_vrc6_1
famistudio_pcm_no_command_vrc6_1:
    lda famistudio_pcm_active+2
    bne famistudio_pcm_not_idle_vrc6_1
    jmp famistudio_pcm_idle_vrc6_1
famistudio_pcm_not_idle_vrc6_1:
    ldx famistudio_pcm_active_offset+2
    lda famistudio_pcm_sample_ptr+2,x
    ora famistudio_pcm_sample_ptr+3,x
    bne famistudio_pcm_read_sample_vrc6_1
    lda famistudio_pcm_active+2
    bpl famistudio_pcm_normal_end_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #64
    sta famistudio_pcm_path+2
.endif
    lda famistudio_pcm_sample_ptr+11,x
    tay
    lda (famistudio_pcm_table_ptr+4),y
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL1_VOL
    rts
famistudio_pcm_normal_end_vrc6_1:
    lda famistudio_pcm_sample_ptr+5,x
    bne famistudio_pcm_not_disable_vrc6_1_2
    jmp famistudio_pcm_disable_vrc6_1
famistudio_pcm_not_disable_vrc6_1_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #2
    sta famistudio_pcm_path+2
.endif
    lda famistudio_pcm_sample_ptr+6,x
    sta famistudio_pcm_sample_ptr+0,x
    lda famistudio_pcm_sample_ptr+7,x
    sta famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+8,x
    sta famistudio_pcm_sample_ptr+2,x
    lda famistudio_pcm_sample_ptr+9,x
    sta famistudio_pcm_sample_ptr+3,x
    lda famistudio_pcm_sample_ptr+10,x
    sta famistudio_pcm_sample_ptr+4,x
famistudio_pcm_read_sample_vrc6_1:
.if FAMISTUDIO_PCM_TRANSIENT_WINDOW
    lda famistudio_pcm_sample_ptr+4,x
    jsr famistudio_pcm_bank_callback
    lda (famistudio_pcm_sample_ptr,x)
    pha
    jsr famistudio_pcm_restore_callback
    pla
.else
    lda famistudio_pcm_sample_ptr+4,x
    cmp famistudio_pcm_current_bank
    beq famistudio_pcm_bank_ready_vrc6_1
    sta famistudio_pcm_current_bank
    jsr famistudio_pcm_bank_callback
famistudio_pcm_bank_ready_vrc6_1:
    lda (famistudio_pcm_sample_ptr,x)
.endif
famistudio_pcm_output_sample_vrc6_1:
    tay
    lda (famistudio_pcm_table_ptr+4),y
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL1_VOL
    inc famistudio_pcm_sample_ptr+0,x
    bne famistudio_pcm_pointer_ready_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #4
    sta famistudio_pcm_path+2
.endif
    inc famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+1,x
    and #FAMISTUDIO_PCM_BANK_MASK
    bne famistudio_pcm_pointer_ready_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #8
    sta famistudio_pcm_path+2
.endif
    lda #>FAMISTUDIO_PCM_WINDOW
    sta famistudio_pcm_sample_ptr+1,x
    inc famistudio_pcm_sample_ptr+4,x
famistudio_pcm_pointer_ready_vrc6_1:
    lda famistudio_pcm_sample_ptr+2,x
    bne famistudio_pcm_count_low_vrc6_1
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #16
    sta famistudio_pcm_path+2
.endif
    dec famistudio_pcm_sample_ptr+3,x
famistudio_pcm_count_low_vrc6_1:
    dec famistudio_pcm_sample_ptr+2,x
    rts
famistudio_pcm_disable_vrc6_1:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #64
    sta famistudio_pcm_path+2
.endif
    lda #0
    sta famistudio_pcm_active+2
    lda #$ff
    sta famistudio_pcm_pending+2
famistudio_pcm_silence_vrc6_1:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+2
    ora #32
    sta famistudio_pcm_path+2
.endif
    lda #$80
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL1_VOL
    rts
famistudio_pcm_idle_vrc6_1:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #32
    sta famistudio_pcm_path+2
.endif
    rts
.endif

.if FAMISTUDIO_USE_VRC6_PCM * (FAMISTUDIO_PCM_VRC6_MASK & 2)
famistudio_pcm_service_vrc6_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #$ff
    sta famistudio_pcm_path+3
.endif
    lda famistudio_pcm_owns+3
    bne famistudio_pcm_owned_vrc6_2
    rts
famistudio_pcm_owned_vrc6_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #0
    sta famistudio_pcm_path+3
.endif
    lda famistudio_song_speed
    bpl famistudio_pcm_service_running_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #128
    sta famistudio_pcm_path+3
.endif
    lda #$80
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL2_VOL
    rts
famistudio_pcm_service_running_vrc6_2:
    lda famistudio_pcm_pending+3
    cmp #$ff
    beq famistudio_pcm_no_command_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    inc famistudio_pcm_path+3
.endif
    cmp #$80
    bne famistudio_pcm_not_disable_vrc6_2_1
    jmp famistudio_pcm_disable_vrc6_2
famistudio_pcm_not_disable_vrc6_2_1:
    sta famistudio_pcm_active_offset+3
    tax
    lda famistudio_pcm_sample_ptr+12,x
    sta famistudio_pcm_active+3
    lda #$ff
    sta famistudio_pcm_pending+3
    ; Newly published descriptors always have a positive length. Even a
    ; constant loop must read its new source byte before using the cache.
    jsr famistudio_pcm_read_initial
    jmp famistudio_pcm_output_sample_vrc6_2
famistudio_pcm_no_command_vrc6_2:
    lda famistudio_pcm_active+3
    bne famistudio_pcm_not_idle_vrc6_2
    jmp famistudio_pcm_idle_vrc6_2
famistudio_pcm_not_idle_vrc6_2:
    ldx famistudio_pcm_active_offset+3
    lda famistudio_pcm_sample_ptr+2,x
    ora famistudio_pcm_sample_ptr+3,x
    bne famistudio_pcm_read_sample_vrc6_2
    lda famistudio_pcm_active+3
    bpl famistudio_pcm_normal_end_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #64
    sta famistudio_pcm_path+3
.endif
    lda famistudio_pcm_sample_ptr+11,x
    tay
    lda (famistudio_pcm_table_ptr+6),y
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL2_VOL
    rts
famistudio_pcm_normal_end_vrc6_2:
    lda famistudio_pcm_sample_ptr+5,x
    bne famistudio_pcm_not_disable_vrc6_2_2
    jmp famistudio_pcm_disable_vrc6_2
famistudio_pcm_not_disable_vrc6_2_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #2
    sta famistudio_pcm_path+3
.endif
    lda famistudio_pcm_sample_ptr+6,x
    sta famistudio_pcm_sample_ptr+0,x
    lda famistudio_pcm_sample_ptr+7,x
    sta famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+8,x
    sta famistudio_pcm_sample_ptr+2,x
    lda famistudio_pcm_sample_ptr+9,x
    sta famistudio_pcm_sample_ptr+3,x
    lda famistudio_pcm_sample_ptr+10,x
    sta famistudio_pcm_sample_ptr+4,x
famistudio_pcm_read_sample_vrc6_2:
.if FAMISTUDIO_PCM_TRANSIENT_WINDOW
    lda famistudio_pcm_sample_ptr+4,x
    jsr famistudio_pcm_bank_callback
    lda (famistudio_pcm_sample_ptr,x)
    pha
    jsr famistudio_pcm_restore_callback
    pla
.else
    lda famistudio_pcm_sample_ptr+4,x
    cmp famistudio_pcm_current_bank
    beq famistudio_pcm_bank_ready_vrc6_2
    sta famistudio_pcm_current_bank
    jsr famistudio_pcm_bank_callback
famistudio_pcm_bank_ready_vrc6_2:
    lda (famistudio_pcm_sample_ptr,x)
.endif
famistudio_pcm_output_sample_vrc6_2:
    tay
    lda (famistudio_pcm_table_ptr+6),y
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL2_VOL
    inc famistudio_pcm_sample_ptr+0,x
    bne famistudio_pcm_pointer_ready_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #4
    sta famistudio_pcm_path+3
.endif
    inc famistudio_pcm_sample_ptr+1,x
    lda famistudio_pcm_sample_ptr+1,x
    and #FAMISTUDIO_PCM_BANK_MASK
    bne famistudio_pcm_pointer_ready_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #8
    sta famistudio_pcm_path+3
.endif
    lda #>FAMISTUDIO_PCM_WINDOW
    sta famistudio_pcm_sample_ptr+1,x
    inc famistudio_pcm_sample_ptr+4,x
famistudio_pcm_pointer_ready_vrc6_2:
    lda famistudio_pcm_sample_ptr+2,x
    bne famistudio_pcm_count_low_vrc6_2
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #16
    sta famistudio_pcm_path+3
.endif
    dec famistudio_pcm_sample_ptr+3,x
famistudio_pcm_count_low_vrc6_2:
    dec famistudio_pcm_sample_ptr+2,x
    rts
famistudio_pcm_disable_vrc6_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #64
    sta famistudio_pcm_path+3
.endif
    lda #0
    sta famistudio_pcm_active+3
    lda #$ff
    sta famistudio_pcm_pending+3
famistudio_pcm_silence_vrc6_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda famistudio_pcm_path+3
    ora #32
    sta famistudio_pcm_path+3
.endif
    lda #$80
    lsr
    lsr
    lsr
    lsr
    ora #$80
    sta FAMISTUDIO_VRC6_PL2_VOL
    rts
famistudio_pcm_idle_vrc6_2:
.if FAMISTUDIO_PCM_TRACK_PATH
    lda #32
    sta famistudio_pcm_path+3
.endif
    rts
.endif
.endif
