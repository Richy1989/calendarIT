package org.hyperstarit.twentyfourseven.calendar

/**
 * Places a day's timed appointments side by side where they overlap: each gets a column within
 * its cluster (a run of appointments that overlap one another, directly or through others) and
 * the cluster's column count, so it takes 1/count of the width.
 */
object TimelineLayout {
    data class Item<T>(val value: T, val start: Long, val end: Long)

    data class Placed<T>(val value: T, val start: Long, val end: Long, val column: Int, val columns: Int)

    /** [minLength] stretches very short ones (as they are drawn) so they don't overlap unseen. */
    fun <T> place(items: List<Item<T>>, minLength: Long = 0): List<Placed<T>> {
        val sorted = items.sortedWith(compareBy<Item<T>>({ it.start }, { -(it.end - it.start) }))
        val result = ArrayList<Placed<T>>(items.size)
        val cluster = ArrayList<Pair<Item<T>, Int>>()
        val columnEnds = ArrayList<Long>()
        var clusterEnd = Long.MIN_VALUE

        fun flush() {
            val count = columnEnds.size.coerceAtLeast(1)
            cluster.forEach { (item, column) -> result += Placed(item.value, item.start, item.end, column, count) }
            cluster.clear()
            columnEnds.clear()
        }

        for (item in sorted) {
            val end = maxOf(item.end, item.start + minLength)
            if (cluster.isNotEmpty() && item.start >= clusterEnd) flush()
            var column = columnEnds.indexOfFirst { it <= item.start }
            if (column < 0) {
                column = columnEnds.size
                columnEnds += end
            } else {
                columnEnds[column] = end
            }
            cluster += item to column
            clusterEnd = if (cluster.size == 1) end else maxOf(clusterEnd, end)
        }
        flush()
        return result
    }
}
