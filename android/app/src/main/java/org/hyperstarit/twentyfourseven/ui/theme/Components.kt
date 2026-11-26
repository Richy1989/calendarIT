package org.hyperstarit.twentyfourseven.ui.theme

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.interaction.collectIsPressedAsState
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.LocalContentColor
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextFieldColors
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.scale
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** The web's canvas: the background with a soft violet glow top-left and a cyan one bottom-right. */
@Composable
fun AmbientBackground(modifier: Modifier = Modifier, content: @Composable BoxScope.() -> Unit) {
    val c = Brand.colors
    Box(
        modifier.fillMaxSize().drawBehind {
            drawRect(c.bg)
            val r1 = size.maxDimension * 0.75f
            drawCircle(
                Brush.radialGradient(listOf(c.violet.copy(alpha = 0.16f * c.glow + 0.04f), Color.Transparent), Offset(size.width * 0.1f, -size.height * 0.06f), r1),
                r1, Offset(size.width * 0.1f, -size.height * 0.06f),
            )
            val r2 = size.maxDimension * 0.65f
            drawCircle(
                Brush.radialGradient(listOf(c.highlight.copy(alpha = 0.12f * c.glow), Color.Transparent), Offset(size.width * 1.04f, size.height * 1.08f), r2),
                r2, Offset(size.width * 1.04f, size.height * 1.08f),
            )
        },
        content = content,
    )
}

/** The brand mark: the 24seven app icon (res/drawable/brand_mark.xml, from docs/icon/corner-store.svg). */
@Composable
fun BrandMark(size: Dp, modifier: Modifier = Modifier) {
    androidx.compose.foundation.Image(
        androidx.compose.ui.res.painterResource(org.hyperstarit.twentyfourseven.R.drawable.brand_mark), contentDescription = null,
        modifier = modifier.size(size),
    )
}

/** A press that sinks a little, like the web's buttons lift on hover. */
@Composable
private fun Modifier.pressScale(source: MutableInteractionSource): Modifier {
    val pressed by source.collectIsPressedAsState()
    val scale by animateFloatAsState(if (pressed) 0.97f else 1f, label = "press")
    return scale(scale)
}

/** The one primary action of a screen: the spectrum gradient with a coloured glow (`.btn-primary`). */
@Composable
fun PrimaryButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    enabled: Boolean = true,
    loading: Boolean = false,
    icon: ImageVector? = null,
) {
    val c = Brand.colors
    val source = remember { MutableInteractionSource() }
    Row(
        modifier
            .pressScale(source)
            .alpha(if (enabled) 1f else 0.55f)
            .shadow(if (enabled) 14.dp else 0.dp, RoundedCornerShape(12.dp), ambientColor = c.highlight, spotColor = c.highlight.copy(alpha = 0.6f * c.glow))
            .clip(RoundedCornerShape(12.dp))
            .background(c.grad)
            .clickable(source, indication = androidx.compose.material3.ripple(), enabled = enabled && !loading, role = Role.Button, onClick = onClick)
            .heightIn(min = 48.dp)
            .padding(horizontal = 20.dp, vertical = 12.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (loading) {
            CircularProgressIndicator(Modifier.size(20.dp), color = c.onAccent, strokeWidth = 2.dp)
        } else {
            icon?.let { Icon(it, null, tint = c.onAccent, modifier = Modifier.size(18.dp).padding(end = 0.dp)); Spacer(Modifier.width(8.dp)) }
            Text(text, color = c.onAccent, style = MaterialTheme.typography.labelLarge, fontSize = 15.sp)
        }
    }
}

/** A quiet secondary action on surface-2 with a hairline (`.btn-ghost`); [danger] for removals (`.btn-danger`). */
@Composable
fun GhostButton(
    text: String,
    onClick: () -> Unit,
    modifier: Modifier = Modifier,
    icon: ImageVector? = null,
    danger: Boolean = false,
    enabled: Boolean = true,
) {
    val c = Brand.colors
    val source = remember { MutableInteractionSource() }
    val ink = if (danger) c.dangerInk else c.text
    Row(
        modifier
            .pressScale(source)
            .alpha(if (enabled) 1f else 0.5f)
            .clip(RoundedCornerShape(10.dp))
            .background(if (danger) c.danger.copy(alpha = 0.1f) else c.surface2)
            .border(1.dp, if (danger) c.danger.copy(alpha = 0.32f) else c.hairline, RoundedCornerShape(10.dp))
            .clickable(source, indication = androidx.compose.material3.ripple(), enabled = enabled, role = Role.Button, onClick = onClick)
            .heightIn(min = 48.dp)
            .padding(horizontal = 14.dp, vertical = 10.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        icon?.let { Icon(it, null, tint = ink, modifier = Modifier.size(18.dp)); Spacer(Modifier.width(8.dp)) }
        Text(text, color = ink, style = MaterialTheme.typography.labelLarge, fontWeight = FontWeight.Medium)
    }
}

/** A small text action in the accent colour, for dialogs and inline links. */
@Composable
fun TextAction(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, color: Color = Brand.colors.link, enabled: Boolean = true) {
    Text(
        text, color = color, style = MaterialTheme.typography.labelLarge,
        modifier = modifier.alpha(if (enabled) 1f else 0.45f).clip(RoundedCornerShape(8.dp))
            .clickable(enabled = enabled, role = Role.Button, onClick = onClick).padding(horizontal = 10.dp, vertical = 10.dp),
    )
}

/** A round icon button on surface-2 (the web's toolbar buttons). */
@Composable
fun RoundIconButton(icon: ImageVector, description: String, onClick: () -> Unit, modifier: Modifier = Modifier, tint: Color = Brand.colors.textDim, filled: Boolean = true) {
    val c = Brand.colors
    Box(
        modifier.size(40.dp).clip(CircleShape)
            .then(if (filled) Modifier.background(c.surface2).border(1.dp, c.hairline, CircleShape) else Modifier)
            .clickable(role = Role.Button, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, description, tint = tint, modifier = Modifier.size(20.dp)) }
}

/** A panel on the canvas: surface, hairline border, the large radius (`.card`). */
@Composable
fun Panel(modifier: Modifier = Modifier, onClick: (() -> Unit)? = null, padding: Dp = 16.dp, border: Color = Brand.colors.hairline, content: @Composable ColumnScope.() -> Unit) {
    val c = Brand.colors
    Column(
        modifier.clip(RoundedCornerShape(18.dp)).background(c.surface).border(1.dp, border, RoundedCornerShape(18.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(padding),
        content = content,
    )
}

/** The web's mono section label: small caps, tracked, faint. */
@Composable
fun SectionLabel(text: String, modifier: Modifier = Modifier, color: Color = Brand.colors.textFaint) {
    Text(text.uppercase(), style = Brand.label, color = color, modifier = modifier)
}

/** A segmented switch (Month · Week · Day · Agenda); the chosen one sits on the soft gradient. */
@Composable
fun <T> Segmented(options: List<Pair<T, String>>, selected: T, onSelect: (T) -> Unit, modifier: Modifier = Modifier) {
    val c = Brand.colors
    Row(
        modifier.clip(RoundedCornerShape(12.dp)).background(c.surface2).border(1.dp, c.hairline, RoundedCornerShape(12.dp)).padding(3.dp),
    ) {
        options.forEach { (value, label) ->
            val on = value == selected
            val ink by animateColorAsState(if (on) c.text else c.textDim, label = "seg")
            Box(
                Modifier.weight(1f).clip(RoundedCornerShape(9.dp))
                    .then(if (on) Modifier.background(c.gradSoft).border(1.dp, c.violet.copy(alpha = 0.5f), RoundedCornerShape(9.dp)) else Modifier)
                    .clickable(role = Role.Tab) { onSelect(value) }.padding(vertical = 8.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text(label, color = ink, style = MaterialTheme.typography.labelLarge, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium, maxLines = 1)
            }
        }
    }
}

/** A top bar on the canvas: back/menu on the left, a display-font title, actions on the right. */
@Composable
fun TopBar(
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    navigation: (@Composable () -> Unit)? = null,
    onTitleClick: (() -> Unit)? = null,
    actions: @Composable RowScope.() -> Unit = {},
) {
    Row(
        modifier.fillMaxWidth().statusBarsPadding().heightIn(min = 60.dp).padding(horizontal = 12.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        navigation?.let { it(); Spacer(Modifier.width(10.dp)) } ?: Spacer(Modifier.width(6.dp))
        Column(
            Modifier.weight(1f).clip(RoundedCornerShape(8.dp))
                .then(if (onTitleClick != null) Modifier.clickable(onClick = onTitleClick) else Modifier).padding(horizontal = 4.dp, vertical = 2.dp),
        ) {
            Text(title, style = MaterialTheme.typography.headlineSmall, fontSize = 23.sp, color = Brand.colors.text, maxLines = 1, overflow = TextOverflow.Ellipsis)
            subtitle?.let { SectionLabel(it, Modifier.padding(top = 1.dp)) }
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically, content = actions)
    }
}

/** A small square tile holding a line icon, for setting and detail rows. */
@Composable
fun IconTile(icon: ImageVector, modifier: Modifier = Modifier, tint: Color = Brand.colors.link, background: Color = Brand.colors.surface2) {
    Box(
        modifier.size(36.dp).clip(RoundedCornerShape(10.dp)).background(background).border(1.dp, Brand.colors.hairline, RoundedCornerShape(10.dp)),
        contentAlignment = Alignment.Center,
    ) { Icon(icon, null, tint = tint, modifier = Modifier.size(19.dp)) }
}

/** A row of a settings list: icon tile, title and explanation, and whatever sits at the end. */
@Composable
fun SettingRow(
    icon: ImageVector,
    title: String,
    modifier: Modifier = Modifier,
    subtitle: String? = null,
    subtitleColor: Color = Brand.colors.textDim,
    onClick: (() -> Unit)? = null,
    trailing: (@Composable () -> Unit)? = null,
) {
    Row(
        modifier.fillMaxWidth().then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(horizontal = 16.dp, vertical = 12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        IconTile(icon)
        Column(Modifier.weight(1f).padding(horizontal = 14.dp)) {
            Text(title, style = MaterialTheme.typography.bodyLarge, fontWeight = FontWeight.Medium, color = Brand.colors.text)
            subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = subtitleColor, modifier = Modifier.padding(top = 2.dp)) }
        }
        trailing?.invoke()
    }
}

@Composable
fun BrandSwitch(checked: Boolean, onCheckedChange: (Boolean) -> Unit, enabled: Boolean = true) {
    val c = Brand.colors
    Switch(
        checked, onCheckedChange, enabled = enabled,
        colors = SwitchDefaults.colors(
            checkedThumbColor = c.onAccent, checkedTrackColor = c.accent, checkedBorderColor = c.accent,
            uncheckedThumbColor = c.textDim, uncheckedTrackColor = c.surface3, uncheckedBorderColor = c.hairlineStrong,
        ),
    )
}

/** A calendar's or category's colour. */
@Composable
fun ColorDot(color: Color, modifier: Modifier = Modifier, size: Dp = 10.dp) {
    Box(modifier.size(size).clip(CircleShape).background(color))
}

/** A round check for tasks: a hairline ring, filled with the spectrum and a check when done. */
@Composable
fun CheckCircle(checked: Boolean, onToggle: () -> Unit, modifier: Modifier = Modifier, size: Dp = 24.dp, ring: Color = Brand.colors.hairlineStrong) {
    val c = Brand.colors
    Box(
        modifier.size(40.dp).clip(CircleShape).clickable(role = Role.Checkbox, onClick = onToggle),
        contentAlignment = Alignment.Center,
    ) {
        Box(
            Modifier.size(size).clip(CircleShape)
                .then(if (checked) Modifier.background(c.grad) else Modifier.border(2.dp, ring, CircleShape)),
            contentAlignment = Alignment.Center,
        ) {
            if (checked) Icon(Glyphs.check, "Done", tint = c.onAccent, modifier = Modifier.size(size * 0.62f))
        }
    }
}

/** A text field on surface-2 with a hairline that lights up in the accent when focused. */
@Composable
fun fieldColors(): TextFieldColors {
    val c = Brand.colors
    return OutlinedTextFieldDefaults.colors(
        focusedContainerColor = c.surface2, unfocusedContainerColor = c.surface2, disabledContainerColor = c.surface2,
        focusedBorderColor = c.accent, unfocusedBorderColor = c.hairline, errorBorderColor = c.danger,
        focusedLabelColor = c.link, unfocusedLabelColor = c.textDim, cursorColor = c.accent,
        focusedTextColor = c.text, unfocusedTextColor = c.text, focusedPlaceholderColor = c.textFaint, unfocusedPlaceholderColor = c.textFaint,
    )
}

@Composable
fun BrandTextField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    modifier: Modifier = Modifier,
    placeholder: String? = null,
    singleLine: Boolean = true,
    minLines: Int = 1,
    keyboardOptions: androidx.compose.foundation.text.KeyboardOptions = androidx.compose.foundation.text.KeyboardOptions.Default,
) {
    OutlinedTextField(
        value, onValueChange, modifier = modifier.fillMaxWidth(), label = { Text(label) },
        placeholder = placeholder?.let { { Text(it) } }, singleLine = singleLine, minLines = minLines,
        shape = RoundedCornerShape(12.dp), colors = fieldColors(), keyboardOptions = keyboardOptions,
    )
}

/** A form row as on the web's event form: a line icon in the gutter, the control beside it. */
@Composable
fun FormRow(icon: ImageVector?, modifier: Modifier = Modifier, onClick: (() -> Unit)? = null, iconTint: Color = Brand.colors.textFaint, content: @Composable RowScope.() -> Unit) {
    Row(
        modifier.fillMaxWidth().heightIn(min = 52.dp).clip(RoundedCornerShape(12.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(horizontal = 8.dp, vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.size(28.dp), contentAlignment = Alignment.Center) {
            if (icon != null) Icon(icon, null, tint = iconTint, modifier = Modifier.size(20.dp))
        }
        Spacer(Modifier.width(14.dp))
        CompositionLocalProvider(LocalContentColor provides Brand.colors.text) { content() }
    }
}

/** A value a row lets you change: on surface-2 like the web's pickers; [menu] adds a chevron. */
@Composable
fun ValueChip(text: String, onClick: () -> Unit, modifier: Modifier = Modifier, mono: Boolean = false, emphasis: Boolean = false, menu: Boolean = false, dot: Color? = null) {
    val c = Brand.colors
    Row(
        modifier.clip(RoundedCornerShape(10.dp)).background(c.surface2).border(1.dp, c.hairline, RoundedCornerShape(10.dp))
            .clickable(role = Role.Button, onClick = onClick).padding(start = 12.dp, end = if (menu) 8.dp else 12.dp, top = 8.dp, bottom = 8.dp),
        horizontalArrangement = Arrangement.Center,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (dot != null) {
            ColorDot(dot)
            Spacer(Modifier.width(8.dp))
        }
        Text(
            text,
            style = if (mono) Brand.data.copy(fontSize = 14.sp) else MaterialTheme.typography.bodyMedium,
            fontWeight = if (emphasis) FontWeight.SemiBold else FontWeight.Medium,
            color = c.text, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f, fill = false),
        )
        if (menu) Icon(Glyphs.chevronDown, null, tint = c.textFaint, modifier = Modifier.padding(start = 4.dp).size(16.dp))
    }
}

/** Something that needs the user: a danger-tinted strip with an icon, the text and an action. */
@Composable
fun Banner(text: String, modifier: Modifier = Modifier, action: String? = null, onClick: (() -> Unit)? = null, warning: Boolean = true) {
    val c = Brand.colors
    val ink = if (warning) c.dangerInk else c.link
    val tone = if (warning) c.danger else c.accent
    Row(
        modifier.fillMaxWidth().clip(RoundedCornerShape(12.dp)).background(tone.copy(alpha = 0.1f))
            .border(1.dp, tone.copy(alpha = 0.3f), RoundedCornerShape(12.dp))
            .then(if (onClick != null) Modifier.clickable(onClick = onClick) else Modifier).padding(horizontal = 14.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Icon(if (warning) Glyphs.alert else Glyphs.info, null, tint = ink, modifier = Modifier.size(18.dp))
        Text(text, color = ink, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.weight(1f).padding(horizontal = 10.dp))
        action?.let { Text(it, color = ink, style = MaterialTheme.typography.labelLarge) }
    }
}

/** A calm empty screen: a faint icon in a soft-gradient disc, a title and a line. */
@Composable
fun EmptyState(icon: ImageVector, title: String, body: String, modifier: Modifier = Modifier) {
    val c = Brand.colors
    Column(modifier.fillMaxWidth().padding(32.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        Box(
            Modifier.size(72.dp).clip(CircleShape).background(c.gradSoft).border(1.dp, c.hairline, CircleShape),
            contentAlignment = Alignment.Center,
        ) { Icon(icon, null, tint = c.link, modifier = Modifier.size(30.dp)) }
        Spacer(Modifier.height(16.dp))
        Text(title, style = MaterialTheme.typography.titleLarge, color = c.text)
        Spacer(Modifier.height(6.dp))
        Text(body, style = MaterialTheme.typography.bodyMedium, color = c.textDim, textAlign = androidx.compose.ui.text.style.TextAlign.Center, modifier = Modifier.widthIn(max = 320.dp))
    }
}

/** A thin hairline divider. */
@Composable
fun Hairline(modifier: Modifier = Modifier, color: Color = Brand.colors.hairline) {
    Box(modifier.fillMaxWidth().height(1.dp).background(color))
}
