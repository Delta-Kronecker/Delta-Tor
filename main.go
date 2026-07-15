package main

import (
	"embed"
	"os"

	"fyne.io/systray"
	"github.com/wailsapp/wails/v2"
	"github.com/wailsapp/wails/v2/pkg/options"
	"github.com/wailsapp/wails/v2/pkg/options/assetserver"
	"github.com/wailsapp/wails/v2/pkg/options/windows"
)

//go:embed all:frontend/dist
var assets embed.FS

//go:embed build/windows/icon.ico
var iconData []byte

func main() {
	app := NewApp()

	go setupTray(app)

	err := wails.Run(&options.App{
		Title:             "Delta Tor 1.3.1",
		Width:             800,
		Height:            1000,
		HideWindowOnClose: true,
		AssetServer: &assetserver.Options{
			Assets: assets,
		},
		BackgroundColour: &options.RGBA{R: 19, G: 23, B: 31, A: 1},
		OnStartup:        app.startup,
		Windows: &windows.Options{
			Theme: windows.Dark,
		},
		Bind: []interface{}{
			app,
		},
	})

	if err != nil {
		println("Error:", err.Error())
	}
}

func setupTray(app *App) {
	systray.Run(
		func() {
			systray.SetIcon(iconData)
			systray.SetTitle("Delta Tor")
			systray.SetTooltip("Delta Tor")

			showItem := systray.AddMenuItem("Show Window", "Show the application window")
			quitItem := systray.AddMenuItem("Quit", "Quit the application")

			go func() {
				for {
					select {
					case <-showItem.ClickedCh:
						app.ShowWindow()
					case <-quitItem.ClickedCh:
						app.StopTor()
						os.Exit(0)
					}
				}
			}()
		},
		nil,
	)
}
