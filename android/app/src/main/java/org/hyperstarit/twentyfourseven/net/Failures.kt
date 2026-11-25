package org.hyperstarit.twentyfourseven.net

import java.security.cert.CertificateException
import javax.net.ssl.SSLException

/**
 * Every throwable behind [this]: causes and suppressed ones. OkHttp tries a host's addresses side
 * by side (IPv6 and IPv4); when they all fail it throws the last failure and attaches the others
 * as suppressed — so a refused certificate can hide behind an unreachable IPv6 address.
 */
fun Throwable.allFailures(): Sequence<Throwable> = sequence {
    val seen = HashSet<Throwable>()
    val pending = ArrayDeque<Throwable>().apply { add(this@allFailures) }
    while (pending.isNotEmpty()) {
        val t = pending.removeFirst()
        if (!seen.add(t)) continue
        yield(t)
        t.cause?.let(pending::add)
        t.suppressed.forEach(pending::add)
    }
}

/** TLS refused the server's certificate (as opposed to the server being unreachable). */
fun Throwable.isCertificateRefusal(): Boolean =
    allFailures().any { it is CertificateException || (it is SSLException && it.cause is CertificateException) || it is javax.net.ssl.SSLHandshakeException || it is javax.net.ssl.SSLPeerUnverifiedException }

fun Throwable.isCertificateChange(): Boolean = allFailures().any { it is CertificateChangedException }
