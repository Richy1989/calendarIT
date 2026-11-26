package org.hyperstarit.twentyfourseven.ui.settings

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.lifecycle.viewmodel.compose.hiltViewModel
import androidx.lifecycle.ViewModel
import dagger.hilt.android.lifecycle.HiltViewModel
import javax.inject.Inject
import kotlin.math.roundToInt
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Hairline
import org.hyperstarit.twentyfourseven.ui.theme.Panel
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.SettingRow
import org.hyperstarit.twentyfourseven.ui.theme.TopBar
import org.hyperstarit.twentyfourseven.widget.WidgetColor
import org.hyperstarit.twentyfourseven.widget.WidgetKind
import org.hyperstarit.twentyfourseven.widget.WidgetLook
import org.hyperstarit.twentyfourseven.widget.WidgetPrefs
import org.hyperstarit.twentyfourseven.widget.pinWidget
import org.hyperstarit.twentyfourseven.widget.refreshWidgets

/** Each widget kind's look, edited as a draft and written (and the placed widgets redrawn) on Save. */
@HiltViewModel
class WidgetSettingsViewModel @Inject constructor(
    private val appScope: CoroutineScope,
    private val prefs: WidgetPrefs,
) : ViewModel() {
    private val saved = WidgetKind.entries.associateWith { prefs.look(it) }.toMutableMap()
    val drafts = mutableStateMapOf<WidgetKind, WidgetLook>().apply { putAll(saved) }

    var justSaved by mutableStateOf(false)
        private set

    val dirty: Boolean get() = WidgetKind.entries.any { drafts[it] != saved[it] }

    fun change(kind: WidgetKind, look: WidgetLook) {
        drafts[kind] = look
        justSaved = false
    }

    fun reset(kind: WidgetKind) = change(kind, WidgetLook.DEFAULT)

    fun save(context: android.content.Context) {
        val changed = WidgetKind.entries.filter { drafts[it] != saved[it] }
        changed.forEach { kind ->
            val look = drafts.getValue(kind)
            prefs.save(kind, look)
            saved[kind] = look
        }
        justSaved = true
        appScope.launch { changed.forEach { refreshWidgets(context, it) } }
    }
}

/** Settings → Widgets: one panel per widget kind (add it, and how it looks), Save at the bottom. */
@Composable
fun WidgetSettingsScreen(onBack: () -> Unit, viewModel: WidgetSettingsViewModel = hiltViewModel()) {
    androidx.activity.compose.BackHandler(onBack = onBack)
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val c = Brand.colors

    Scaffold(
        containerColor = Color.Transparent,
        topBar = { TopBar("Widgets", navigation = { RoundIconButton(Glyphs.back, "Back", onBack) }) },
        bottomBar = {
            Box(Modifier.fillMaxWidth().background(c.surface.copy(alpha = 0.96f)).navigationBarsPadding().padding(16.dp)) {
                PrimaryButton(
                    if (viewModel.justSaved && !viewModel.dirty) "Saved" else "Save",
                    onClick = { viewModel.save(context.applicationContext) },
                    enabled = viewModel.dirty,
                    icon = if (viewModel.justSaved && !viewModel.dirty) Glyphs.check else null,
                    modifier = Modifier.fillMaxWidth(),
                )
            }
        },
    ) { padding ->
        Column(
            Modifier.fillMaxSize().padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp).padding(bottom = 24.dp),
            verticalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            WidgetKind.entries.forEach { kind ->
                val look = viewModel.drafts.getValue(kind)
                SectionLabel(kind.title, Modifier.padding(start = 4.dp, top = 8.dp))
                Panel(padding = 0.dp) {
                    SettingRow(
                        Glyphs.widget, "Add to home screen",
                        subtitle = kind.description,
                        onClick = { scope.launch { pinWidget(context, kind) } },
                        trailing = { Icon(Glyphs.plus, "Add widget", tint = c.textFaint, modifier = Modifier.size(20.dp)) },
                    )
                    Hairline()
                    Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                        when (kind) {
                            WidgetKind.MONTH -> MonthPreview(look)
                        }
                        PercentSlider("Background", look.background, solidAt100 = true) { viewModel.change(kind, look.copy(background = it)) }
                        ColorSetting("Lines between days", look.border, themeColor = c.text) { viewModel.change(kind, look.copy(border = it)) }
                        ColorSetting("Today", look.today, themeColor = c.accent) { viewModel.change(kind, look.copy(today = it)) }
                        Text(
                            "Reset to default", style = MaterialTheme.typography.labelLarge, color = c.link,
                            modifier = Modifier.clip(RoundedCornerShape(8.dp)).clickable { viewModel.reset(kind) }.padding(vertical = 4.dp),
                        )
                    }
                }
            }
        }
    }
}

/** A label with its value and a 0–100 % slider in steps of 5. */
@Composable
private fun PercentSlider(label: String, value: Int, solidAt100: Boolean = false, onChange: (Int) -> Unit) {
    val c = Brand.colors
    Column {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text(label, style = MaterialTheme.typography.bodyMedium, color = c.text, modifier = Modifier.weight(1f))
            Text(
                when {
                    solidAt100 && value >= 100 -> "Solid"
                    value <= 0 -> "Transparent"
                    else -> "$value %"
                },
                style = Brand.data, color = c.textFaint,
            )
        }
        Slider(value = value.toFloat(), onValueChange = { onChange((it / 5).roundToInt() * 5) }, valueRange = 0f..100f, steps = 19)
    }
}

/** A colour: the theme's own or one of a few, and how opaque it is (0 = not drawn). */
@Composable
private fun ColorSetting(label: String, value: WidgetColor, themeColor: Color, onChange: (WidgetColor) -> Unit) {
    val c = Brand.colors
    Column {
        PercentSlider(label, value.alpha) { onChange(value.copy(alpha = it)) }
        Row(Modifier.horizontalScroll(rememberScrollState()).padding(top = 2.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            Swatch(themeColor, "Theme", selected = value.rgb == null) { onChange(value.copy(rgb = null, alpha = value.alpha.coerceAtLeast(5))) }
            SWATCHES.forEach { rgb ->
                Swatch(Color(0xFF000000.toInt() or rgb), null, selected = value.rgb == rgb) { onChange(value.copy(rgb = rgb, alpha = value.alpha.coerceAtLeast(5))) }
            }
        }
        if (value.alpha <= 0) Text("Not drawn.", style = MaterialTheme.typography.bodySmall, color = c.textDim, modifier = Modifier.padding(top = 4.dp))
    }
}

/** A round colour to pick; the theme's own is a pill that says so. */
@Composable
private fun Swatch(color: Color, label: String?, selected: Boolean, onClick: () -> Unit) {
    val c = Brand.colors
    val shape = RoundedCornerShape(99.dp)
    Row(
        Modifier.height(34.dp).clip(shape)
            .border(if (selected) 2.dp else 1.dp, if (selected) c.accent else c.hairlineStrong, shape)
            .clickable(role = Role.RadioButton, onClickLabel = label, onClick = onClick)
            .padding(4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(26.dp).clip(CircleShape).background(color).border(1.dp, c.hairline, CircleShape))
        if (label != null) {
            Text(label, style = MaterialTheme.typography.labelLarge, color = if (selected) c.text else c.textDim, modifier = Modifier.padding(start = 8.dp, end = 8.dp))
        }
    }
}

/** Three weeks drawn the way the widget draws them, over a stand-in wallpaper. */
@Composable
private fun MonthPreview(look: WidgetLook) {
    val p = Brand.colors
    fun resolve(w: WidgetColor, themed: Color) = if (w.alpha <= 0) null else (w.rgb?.let { Color(0xFF000000.toInt() or it) } ?: themed).copy(alpha = w.alpha / 100f)
    val border = resolve(look.border, p.text)
    val today = resolve(look.today, p.accent)
    Box(
        Modifier.fillMaxWidth().height(150.dp).clip(RoundedCornerShape(14.dp))
            .background(Brush.linearGradient(listOf(p.violet, p.accent, p.highlight))),
    ) {
        Column(Modifier.fillMaxSize().padding(10.dp).clip(RoundedCornerShape(10.dp)).background(p.bg.copy(alpha = look.background / 100f)).padding(4.dp)) {
            repeat(3) { row ->
                Row(Modifier.fillMaxWidth().weight(1f)) {
                    repeat(7) { col ->
                        Row(Modifier.weight(1f).fillMaxHeight()) {
                            if (col > 0 && border != null) Box(Modifier.width(1.dp).fillMaxHeight().background(border))
                            Column(Modifier.weight(1f).fillMaxHeight().then(if (row == 1 && col == 3 && today != null) Modifier.background(today) else Modifier)) {
                                if (row > 0 && border != null) Box(Modifier.fillMaxWidth().height(1.dp).background(border))
                                Text(
                                    "${row * 7 + col + 1}", fontSize = 10.sp, color = p.text, modifier = Modifier.fillMaxWidth().padding(top = 2.dp),
                                    fontWeight = if (row == 1 && col == 3) FontWeight.Bold else FontWeight.Normal,
                                    textAlign = androidx.compose.ui.text.style.TextAlign.Center,
                                )
                                if ((row + col) % 3 == 0) {
                                    Spacer(Modifier.height(2.dp))
                                    Box(Modifier.fillMaxWidth().padding(horizontal = 2.dp).height(5.dp).clip(RoundedCornerShape(2.dp)).background(p.accent.copy(alpha = 0.5f)))
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** User-picked colours (0xRRGGBB): light, dark and a few hues — not theme tokens, they're the user's choice. */
private val SWATCHES = listOf(0xFFFFFF, 0x000000, 0x8A8A8A, 0xFF6B5A, 0xF5A623, 0xF2D648, 0x2BB673, 0x22B8CF, 0x3D7BFF, 0x9B59D0, 0xE84393)
