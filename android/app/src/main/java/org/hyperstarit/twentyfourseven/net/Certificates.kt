package org.hyperstarit.twentyfourseven.net

import android.annotation.SuppressLint
import java.net.InetSocketAddress
import java.security.KeyStore
import java.security.MessageDigest
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import java.time.Instant
import javax.net.ssl.HostnameVerifier
import javax.net.ssl.HttpsURLConnection
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.TrustManager
import javax.net.ssl.TrustManagerFactory
import javax.net.ssl.X509TrustManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.HttpUrl
import okhttp3.OkHttpClient

/** What the user is shown before trusting a certificate no authority vouches for. */
data class CertificateInfo(
    /** SHA-256 of the certificate (DER), lowercase hex — what gets pinned. */
    val sha256: String,
    val subject: String,
    val issuer: String,
    val validFrom: Instant,
    val validUntil: Instant,
    val selfSigned: Boolean,
) {
    /** "AB:CD:…", the way servers and browsers print it, for comparing by eye. */
    val fingerprint: String get() = sha256.uppercase().chunked(2).joinToString(":")
}

/** The server showed a certificate other than the one the user trusted for this account. */
class CertificateChangedException(val presented: String) :
    CertificateException("The server's certificate has changed.")

object Certificates {
    fun sha256(cert: X509Certificate): String =
        MessageDigest.getInstance("SHA-256").digest(cert.encoded).joinToString("") { "%02x".format(it) }

    fun info(cert: X509Certificate) = CertificateInfo(
        sha256 = sha256(cert),
        subject = cert.subjectX500Principal.name,
        issuer = cert.issuerX500Principal.name,
        validFrom = cert.notBefore.toInstant(),
        validUntil = cert.notAfter.toInstant(),
        selfSigned = cert.subjectX500Principal == cert.issuerX500Principal,
    )

    private fun systemTrust(): X509TrustManager {
        val factory = TrustManagerFactory.getInstance(TrustManagerFactory.getDefaultAlgorithm())
        factory.init(null as KeyStore?)
        return factory.trustManagers.filterIsInstance<X509TrustManager>().single()
    }

    /**
     * A client for one account. Without a pin it is [base] as it is: the system's authorities only.
     * With one, a certificate the system trusts still passes, and otherwise exactly the pinned
     * certificate does — whatever name it carries (a home server is often reached by a bare IP).
     * Any other is refused with [CertificateChangedException], before anything is sent.
     */
    fun clientFor(base: OkHttpClient, pin: String?): OkHttpClient {
        if (pin == null) return base
        val system = systemTrust()
        // Android's own check first; only what it refuses is compared with the pin.
        @SuppressLint("CustomX509TrustManager")
        val trust = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) =
                system.checkClientTrusted(chain, authType)

            override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
                try {
                    system.checkServerTrusted(chain, authType)
                } catch (e: CertificateException) {
                    val presented = sha256(chain.first())
                    if (presented != pin) throw CertificateChangedException(presented)
                }
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = system.acceptedIssuers
        }
        val context = SSLContext.getInstance("TLS").apply { init(null, arrayOf<TrustManager>(trust), null) }
        val defaultNames = HttpsURLConnection.getDefaultHostnameVerifier()
        val names = HostnameVerifier { host, session ->
            val leaf = session.peerCertificates.firstOrNull() as? X509Certificate
            (leaf != null && sha256(leaf) == pin) || defaultNames.verify(host, session)
        }
        return base.newBuilder()
            .sslSocketFactory(context.socketFactory, trust)
            .hostnameVerifier(names)
            .build()
    }

    /**
     * Reads the certificate [url]'s server presents. It is recorded and then refused, so the
     * handshake always fails: nothing can ever be sent over this connection.
     */
    suspend fun probe(url: HttpUrl): CertificateInfo = withContext(Dispatchers.IO) {
        val captured = arrayOfNulls<X509Certificate>(1)
        @SuppressLint("CustomX509TrustManager")
        val recordAndRefuse = object : X509TrustManager {
            override fun checkClientTrusted(chain: Array<out X509Certificate>, authType: String) =
                throw CertificateException("Not a client.")

            override fun checkServerTrusted(chain: Array<out X509Certificate>, authType: String) {
                captured[0] = chain.first()
                throw CertificateException("Only reading the certificate.")
            }

            override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
        }
        val context = SSLContext.getInstance("TLS").apply { init(null, arrayOf<TrustManager>(recordAndRefuse), null) }
        val plain = java.net.Socket()
        plain.connect(InetSocketAddress(url.host, url.port), 15_000)
        plain.soTimeout = 15_000
        try {
            (context.socketFactory.createSocket(plain, url.host, url.port, true) as SSLSocket).use { it.startHandshake() }
        } catch (_: javax.net.ssl.SSLException) {
            // Expected: the certificate was refused on purpose once read.
        }
        info(captured[0] ?: throw CertificateException("The server presented no certificate."))
    }
}
