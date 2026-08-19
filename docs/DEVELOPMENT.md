# Development workflow

Current host loop: `edit -> validate_repo.py -> dotnet build -> dotnet test -> PR`.

Later XR loop: `Core tests -> Unity fake input -> XR Simulator where applicable -> Horizon Link -> Quest APK -> device logs/metrics`.

Passthrough, Android permissions, ARM64 inference, thermal behavior, spatial stability, readability, fatigue, and cognitive load are not validated by host tests.
