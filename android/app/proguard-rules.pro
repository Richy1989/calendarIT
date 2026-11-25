# kotlinx.serialization keeps what it needs through its own consumer rules; the API models are
# reached only through generated serializers.
-keepattributes *Annotation*, InnerClasses

# ical4j: repeat rules now, whole iCalendar objects with CalDAV later. It finds parts of itself by
# reflection and ServiceLoader, so it is kept whole. Its own zone-rules provider plugs into a JDK
# API Android lacks (zones come from java.time here), hence the dontwarn.
-keep class net.fortuna.ical4j.** { *; }
-dontwarn java.time.zone.ZoneRulesProvider
-dontwarn org.slf4j.**
# ical4j's optional add-ons (Groovy builder DSL, Caffeine zone cache, jparsec filter expressions)
# are kept with it but never used; their libraries aren't included.
-dontwarn groovy.**
-dontwarn org.codehaus.groovy.**
-dontwarn com.github.benmanes.caffeine.**
-dontwarn org.jparsec.**
-dontwarn org.joda.convert.**
-dontwarn java.beans.**
