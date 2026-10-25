pluginManagement {
    repositories {
        google {
            content {
                includeGroupByRegex("com\\.android.*")
                includeGroupByRegex("com\\.google.*")
                includeGroupByRegex("androidx.*")
            }
        }
        mavenCentral()
        gradlePluginPortal()
    }
}
plugins {
    id("org.gradle.toolchains.foojay-resolver-convention") version "0.9.0"
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
        // Tapsell Mediation SDK and its adapters (only resolved for their groups)
        maven("https://maven.tapsell.ir") {
            content { includeGroupByRegex("ir\\.tapsell.*") }
        }
    }
}

rootProject.name = "DeltaTor"
include(":app")