package com.panelos.panelhome.domain.model

import com.panelos.panelhome.domain.protocol.TouchCommandAction

data class DesktopTouchEvent(
    val action: TouchCommandAction,
    val pointerId: Int = 0,
    val x: Double? = null,
    val y: Double? = null,
    val generation: Long,
)
