# Snowflake / gomobile bindings
-keep class snowflake.** { *; }
-keep class go.** { *; }

# Native methods
-keepclasseswithmembernames class * {
    native <methods>;
}