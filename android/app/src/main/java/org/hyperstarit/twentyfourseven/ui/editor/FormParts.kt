package org.hyperstarit.twentyfourseven.ui.editor

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DatePicker
import androidx.compose.material3.DatePickerDefaults
import androidx.compose.material3.DatePickerDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TimePicker
import androidx.compose.material3.TimePickerDefaults
import androidx.compose.material3.rememberDatePickerState
import androidx.compose.material3.rememberTimePickerState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.unit.dp
import androidx.compose.ui.window.Dialog
import androidx.compose.ui.window.DialogProperties
import java.time.LocalDate
import java.time.LocalTime
import java.time.ZoneOffset
import org.hyperstarit.twentyfourseven.ui.theme.AmbientBackground
import org.hyperstarit.twentyfourseven.ui.theme.ColorDot
import org.hyperstarit.twentyfourseven.ui.theme.Glyphs
import org.hyperstarit.twentyfourseven.ui.theme.Brand
import org.hyperstarit.twentyfourseven.ui.theme.PrimaryButton
import org.hyperstarit.twentyfourseven.ui.theme.RoundIconButton
import org.hyperstarit.twentyfourseven.ui.theme.SectionLabel
import org.hyperstarit.twentyfourseven.ui.theme.TextAction
import org.hyperstarit.twentyfourseven.ui.theme.ValueChip

/**
 * A full-screen editor on the canvas: close on the left, what it edits in the middle, Save on the
 * right; the form scrolls beneath, above the keyboard.
 */
@Composable
fun EditorScaffold(kind: String, onClose: () -> Unit, onSave: () -> Unit, saveEnabled: Boolean, content: @Composable () -> Unit) {
    Dialog(onDismissRequest = onClose, properties = DialogProperties(usePlatformDefaultWidth = false, decorFitsSystemWindows = false)) {
        AmbientBackground {
            Column(Modifier.fillMaxSize().imePadding()) {
                Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 12.dp, vertical = 10.dp), verticalAlignment = Alignment.CenterVertically) {
                    RoundIconButton(Glyphs.close, "Cancel", onClose)
                    SectionLabel(kind, Modifier.weight(1f).padding(horizontal = 14.dp))
                    PrimaryButton("Save", onSave, enabled = saveEnabled, icon = Glyphs.check)
                }
                Column(
                    Modifier.fillMaxSize().verticalScroll(rememberScrollState()).navigationBarsPadding().padding(horizontal = 14.dp).padding(bottom = 32.dp),
                ) { content() }
            }
        }
    }
}

/** A borderless text field with a faint placeholder, as on the web's forms. */
@Composable
fun InlineField(
    value: String,
    onValueChange: (String) -> Unit,
    placeholder: String,
    modifier: Modifier = Modifier,
    style: TextStyle = MaterialTheme.typography.bodyLarge,
    singleLine: Boolean = true,
    minLines: Int = 1,
    capitalize: Boolean = true,
) {
    val c = Brand.colors
    BasicTextField(
        value, onValueChange, modifier = modifier.fillMaxWidth().padding(vertical = 8.dp),
        textStyle = style.copy(color = c.text), singleLine = singleLine, minLines = minLines,
        cursorBrush = SolidColor(c.accent),
        keyboardOptions = KeyboardOptions(capitalization = if (capitalize) KeyboardCapitalization.Sentences else KeyboardCapitalization.None),
        decorationBox = { inner ->
            Box {
                if (value.isEmpty()) Text(placeholder, style = style, color = c.textFaint)
                inner()
            }
        },
    )
}

/** A value with a menu of choices; [dots] puts a colour before a choice (calendars, categories). */
@Composable
fun <T> ChoiceChip(shown: String, options: List<Pair<T, String>>, onPick: (T) -> Unit, modifier: Modifier = Modifier, dots: Map<T, Color> = emptyMap(), selected: T? = null) {
    val c = Brand.colors
    var open by remember { mutableStateOf(false) }
    Box(modifier) {
        ValueChip(shown, { open = true }, menu = true, dot = selected?.let { dots[it] })
        DropdownMenu(expanded = open, onDismissRequest = { open = false }, containerColor = c.surface2, shape = RoundedCornerShape(14.dp)) {
            options.forEach { (value, name) ->
                DropdownMenuItem(
                    text = { Text(name, color = c.text) },
                    leadingIcon = dots[value]?.let { color -> { ColorDot(color, size = 10.dp) } },
                    onClick = { open = false; onPick(value) },
                )
            }
        }
    }
}

/** A labelled line inside a form row: the label on the left, the controls on the right. */
@Composable
fun LabeledLine(label: String, modifier: Modifier = Modifier, controls: @Composable () -> Unit) {
    Row(modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, style = MaterialTheme.typography.bodyLarge, color = Brand.colors.textDim, modifier = Modifier.weight(1f))
        controls()
    }
}

@Composable
fun AddLink(text: String, onClick: () -> Unit) {
    val c = Brand.colors
    Row(verticalAlignment = Alignment.CenterVertically) {
        Icon(Glyphs.plus, null, tint = c.link, modifier = Modifier.size(16.dp))
        TextAction(text, onClick, Modifier.padding(start = 0.dp))
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PickDate(initial: LocalDate, onDone: () -> Unit, onPick: (LocalDate) -> Unit) {
    val c = Brand.colors
    val state = rememberDatePickerState(initialSelectedDateMillis = initial.atStartOfDay(ZoneOffset.UTC).toInstant().toEpochMilli())
    val colors = DatePickerDefaults.colors(
        containerColor = c.surface, selectedDayContainerColor = c.accent, selectedDayContentColor = c.onAccent,
        todayDateBorderColor = c.highlight, todayContentColor = c.highlight,
    )
    DatePickerDialog(
        onDismissRequest = onDone,
        colors = colors,
        confirmButton = {
            TextAction("OK", {
                state.selectedDateMillis?.let { onPick(java.time.Instant.ofEpochMilli(it).atZone(ZoneOffset.UTC).toLocalDate()) }
                onDone()
            })
        },
        dismissButton = { TextAction("Cancel", onDone, color = c.textDim) },
    ) { DatePicker(state, colors = colors) }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PickTime(initial: LocalTime, is24h: Boolean, onDone: () -> Unit, dismissLabel: String = "Cancel", onDismissAction: () -> Unit = onDone, onPick: (LocalTime) -> Unit) {
    val c = Brand.colors
    val state = rememberTimePickerState(initial.hour, initial.minute, is24h)
    AlertDialog(
        onDismissRequest = onDone,
        containerColor = c.surface,
        confirmButton = { TextAction("OK", { onPick(LocalTime.of(state.hour, state.minute)); onDone() }) },
        dismissButton = { TextAction(dismissLabel, onDismissAction, color = c.textDim) },
        text = {
            TimePicker(
                state,
                colors = TimePickerDefaults.colors(
                    clockDialColor = c.surface2, selectorColor = c.accent, periodSelectorBorderColor = c.hairline,
                ),
            )
        },
    )
}

@Composable
fun Gap(width: Int = 8) = Spacer(Modifier.width(width.dp))

@Composable
fun FormDot(color: Color) = Box(Modifier.size(10.dp).background(color, RoundedCornerShape(5.dp)))
