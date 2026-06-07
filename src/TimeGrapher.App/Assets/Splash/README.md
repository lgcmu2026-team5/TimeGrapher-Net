# Splash frames

`splash_0001.png` … `splash_0122.png` (640x360, 30 fps, ~4.07 s) are the frames the
app actually ships; `TimeGrapher.App.csproj` embeds only `Assets\Splash\*.png` as
Avalonia resources.

`Source/splash5.mp4` is the editing source those PNG frames were extracted from.
It is **not** embedded in the application — it is kept in the repository only so the
frames can be regenerated (e.g. with `ffmpeg -i splash5.mp4 splash_%04d.png`).
