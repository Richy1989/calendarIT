package org.hyperstarit.twentyfourseven.ui.accounts

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.padding
import androidx.compose.ui.draw.clip
import androidx.compose.ui.unit.dp
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import org.hyperstarit.twentyfourseven.net.CertificateInfo

/**
 * Shows a certificate no authority vouches for, so the user can compare its fingerprint with the
 * one their server shows, and trust exactly that certificate for this account.
 */
@Composable
fun CertificateDialog(info: CertificateInfo, changed: Boolean, onTrust: () -> Unit, onCancel: () -> Unit) {
    val date = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withZone(ZoneId.systemDefault())
    val c = org.hyperstarit.twentyfourseven.ui.theme.Brand.colors
    AlertDialog(
        onDismissRequest = onCancel,
        icon = { androidx.compose.material3.Icon(org.hyperstarit.twentyfourseven.ui.theme.Glyphs.shield, null, tint = if (changed) c.dangerInk else c.link) },
        title = { Text(if (changed) "Certificate changed" else "Untrusted certificate") },
        text = {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                Text(
                    if (changed) {
                        "This server now shows a different certificate than the one you trusted. If you didn't " +
                            "replace it yourself, someone may be listening in — don't trust it."
                    } else {
                        "No certificate authority vouches for this server" +
                            (if (info.selfSigned) " (the certificate is self-signed)" else "") +
                            ". Trust it only if the fingerprint matches the one your server shows."
                    },
                )
                Text("Fingerprint (SHA-256)", style = MaterialTheme.typography.labelMedium)
                Text(
                    info.fingerprint, style = org.hyperstarit.twentyfourseven.ui.theme.Brand.data, color = c.text,
                    modifier = androidx.compose.ui.Modifier.clip(androidx.compose.foundation.shape.RoundedCornerShape(10.dp))
                        .background(c.surface).padding(10.dp),
                )
                Text("Issued to: ${info.subject}", style = MaterialTheme.typography.bodySmall)
                Text("Issued by: ${info.issuer}", style = MaterialTheme.typography.bodySmall)
                Text(
                    "Valid ${date.format(info.validFrom)} – ${date.format(info.validUntil)}",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        },
        confirmButton = { org.hyperstarit.twentyfourseven.ui.theme.TextAction("Trust this certificate", onTrust, color = if (changed) c.dangerInk else c.link) },
        dismissButton = { org.hyperstarit.twentyfourseven.ui.theme.TextAction("Cancel", onCancel, color = c.textDim) },
    )
}
