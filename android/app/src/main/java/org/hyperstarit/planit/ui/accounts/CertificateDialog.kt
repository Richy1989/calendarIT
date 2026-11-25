package org.hyperstarit.planit.ui.accounts

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.time.format.FormatStyle
import org.hyperstarit.planit.net.CertificateInfo

/**
 * Shows a certificate no authority vouches for, so the user can compare its fingerprint with the
 * one their server shows, and trust exactly that certificate for this account.
 */
@Composable
fun CertificateDialog(info: CertificateInfo, changed: Boolean, onTrust: () -> Unit, onCancel: () -> Unit) {
    val date = DateTimeFormatter.ofLocalizedDate(FormatStyle.MEDIUM).withZone(ZoneId.systemDefault())
    AlertDialog(
        onDismissRequest = onCancel,
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
                Text(info.fingerprint, fontFamily = FontFamily.Monospace, style = MaterialTheme.typography.bodySmall)
                Text("Issued to: ${info.subject}", style = MaterialTheme.typography.bodySmall)
                Text("Issued by: ${info.issuer}", style = MaterialTheme.typography.bodySmall)
                Text(
                    "Valid ${date.format(info.validFrom)} – ${date.format(info.validUntil)}",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        },
        confirmButton = { TextButton(onClick = onTrust) { Text("Trust this certificate") } },
        dismissButton = { TextButton(onClick = onCancel) { Text("Cancel") } },
    )
}
