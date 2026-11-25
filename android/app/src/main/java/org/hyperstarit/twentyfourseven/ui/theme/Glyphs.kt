package org.hyperstarit.twentyfourseven.ui.theme

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.graphics.vector.addPathNodes
import androidx.compose.ui.unit.dp

/**
 * The web app's line icons (web/src/components/FormIcons.tsx and friends): stroke-only on a 24 grid,
 * round caps. Tinted by Icon like any other vector.
 */
object Glyphs {
    private fun circle(cx: Float, cy: Float, r: Float) = "M${cx - r} ${cy}a$r $r 0 1 0 ${2 * r} 0a$r $r 0 1 0 ${-2 * r} 0"

    private fun icon(name: String, vararg paths: String, stroke: Float = 1.8f): ImageVector =
        ImageVector.Builder(name, 24.dp, 24.dp, 24f, 24f).apply {
            paths.forEach { d ->
                addPath(
                    pathData = addPathNodes(d),
                    stroke = SolidColor(Color.Black),
                    strokeLineWidth = stroke,
                    strokeLineCap = StrokeCap.Round,
                    strokeLineJoin = StrokeJoin.Round,
                )
            }
        }.build()

    val clock = icon("clock", circle(12f, 12f, 9f), "M12 7v5l3 2")
    val repeat = icon("repeat", "M17 2l4 4-4 4", "M3 11V9a3 3 0 0 1 3-3h15", "M7 22l-4-4 4-4", "M21 13v2a3 3 0 0 1-3 3H3")
    val users = icon("users", circle(9f, 8f, 3.5f), "M2.5 20a6.5 6.5 0 0 1 13 0", "M16 4.5a3.5 3.5 0 0 1 0 7", "M18 14.5a6.5 6.5 0 0 1 3.5 5.5")
    val pin = icon("pin", "M12 21s-7-6.2-7-11.5a7 7 0 0 1 14 0C19 14.8 12 21 12 21z", circle(12f, 9.5f, 2.5f))
    val text = icon("text", "M4 6h16", "M4 12h16", "M4 18h10")
    val bell = icon("bell", "M6 16V11a6 6 0 0 1 12 0v5l2 2H4z", "M10 20a2 2 0 0 0 4 0")
    val close = icon("close", "M6 6l12 12", "M18 6L6 18")
    val chevronDown = icon("chevronDown", "M6 9l6 6 6-6")
    val chevronLeft = icon("chevronLeft", "M15 18l-6-6 6-6")
    val chevronRight = icon("chevronRight", "M9 18l6-6-6-6")
    val back = icon("back", "M19 12H5", "M12 19l-7-7 7-7")
    val calendar = icon("calendar", "M5.5 4.5h13a2 2 0 0 1 2 2v12.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2V6.5a2 2 0 0 1 2-2z", "M3.5 10h17", "M8 2.5v4", "M16 2.5v4")
    val tasks = icon("tasks", "M9 11l3 3 8-8", "M20 12v6a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h9")
    val settings = icon("settings", "M4 21v-7", "M4 10V3", "M12 21v-9", "M12 8V3", "M20 21v-5", "M20 12V3", "M1.5 14h5", "M9.5 8h5", "M17.5 16h5")
    val plus = icon("plus", "M12 5v14", "M5 12h14", stroke = 2.2f)
    val menu = icon("menu", "M4 6h16", "M4 12h16", "M4 18h16")
    val sync = icon("sync", "M20.5 12a8.5 8.5 0 0 1-14.8 5.7", "M3.5 12a8.5 8.5 0 0 1 14.8-5.7", "M19 2.5v4h-4", "M5 21.5v-4h4")
    val trash = icon("trash", "M3.5 6h17", "M9 6V4h6v2", "M18.5 6l-.9 13.1a2 2 0 0 1-2 1.9H8.4a2 2 0 0 1-2-1.9L5.5 6", "M10 10.5v6", "M14 10.5v6")
    val edit = icon("edit", "M16.5 3.5l4 4L8 20H4v-4z", "M14 6l4 4")
    val user = icon("user", circle(12f, 8f, 4f), "M4 21a8 8 0 0 1 16 0")
    val check = icon("check", "M5 12.5l4.5 4.5L19 7.5", stroke = 2.4f)
    val more = icon("more", "M12 5.5h.01", "M12 12h.01", "M12 18.5h.01", stroke = 3f)
    val alert = icon("alert", "M10.3 3.9L2.4 18a2 2 0 0 0 1.7 3h15.8a2 2 0 0 0 1.7-3L13.7 3.9a2 2 0 0 0-3.4 0z", "M12 9.5v4", "M12 17.2v.01")
    val contrast = icon("contrast", circle(12f, 12f, 9f), "M12 3a9 9 0 0 1 0 18z")
    val widget = icon("widget", "M4 4h6.5v6.5H4z", "M13.5 4H20v6.5h-6.5z", "M4 13.5h6.5V20H4z", "M13.5 13.5H20V20h-6.5z")
    val phone = icon("phone", "M7.5 2.5h9a2 2 0 0 1 2 2v15a2 2 0 0 1-2 2h-9a2 2 0 0 1-2-2v-15a2 2 0 0 1 2-2z", "M11 18h2")
    val zap = icon("zap", "M13 2.5L4.5 13.5h6.5l-1 8 8.5-11h-6.5z")
    val flag = icon("flag", "M5 21V4", "M5 4h11.5l-2 4 2 4H5")
    val server = icon("server", "M5 3.5h14a1.5 1.5 0 0 1 1.5 1.5v4a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 9V5A1.5 1.5 0 0 1 5 3.5z", "M5 13.5h14a1.5 1.5 0 0 1 1.5 1.5v4a1.5 1.5 0 0 1-1.5 1.5H5A1.5 1.5 0 0 1 3.5 19v-4A1.5 1.5 0 0 1 5 13.5z", "M7.5 7h.01", "M7.5 17h.01")
    val shield = icon("shield", "M12 3l8 3v6c0 4.8-3.4 7.9-8 9-4.6-1.1-8-4.2-8-9V6z", "M9 12l2 2 4-4")
    val globe = icon("globe", circle(12f, 12f, 9f), "M3 12h18", "M12 3a14 14 0 0 1 0 18", "M12 3a14 14 0 0 0 0 18")
    val mail = icon("mail", "M4.5 5.5h15a1.5 1.5 0 0 1 1.5 1.5v10a1.5 1.5 0 0 1-1.5 1.5h-15A1.5 1.5 0 0 1 3 17V7a1.5 1.5 0 0 1 1.5-1.5z", "M3.5 7l8.5 6 8.5-6")
    val folder = icon("folder", "M3.5 7a2 2 0 0 1 2-2h4l2 2.5h7a2 2 0 0 1 2 2V17a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2z")
    val tag = icon("tag", "M3.5 12.2V4.5a1 1 0 0 1 1-1h7.7l8.3 8.3a1.5 1.5 0 0 1 0 2.1l-6.2 6.2a1.5 1.5 0 0 1-2.1 0z", "M8 8h.01")
    val today = icon("today", "M5.5 4.5h13a2 2 0 0 1 2 2v12.5a2 2 0 0 1-2 2h-13a2 2 0 0 1-2-2V6.5a2 2 0 0 1 2-2z", "M3.5 10h17", "M8 2.5v4", "M16 2.5v4", "M10.5 13.5h3v3h-3z")
    val info = icon("info", circle(12f, 12f, 9f), "M12 11v5.5", "M12 7.8v.01")
}
