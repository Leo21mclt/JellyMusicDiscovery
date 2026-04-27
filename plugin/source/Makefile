.PHONY: build package clean restore

restore:
	dotnet restore

build: restore
	dotnet build -c Release

package: build
	mkdir -p ../artifacts
	cp meta.json bin/Release/net9.0/meta.json
	# TagLibSharp.dll MUST ship — Jellyfin doesn't bundle it, and the
	# plugin's audio-tag readers will throw FileNotFoundException at
	# runtime without it. The csproj's StripUnneededDlls target already
	# strips the Jellyfin/Microsoft/System DLLs that ARE bundled by the
	# host, so whatever's left in bin/Release/net9.0 is what we ship.
	cd bin/Release/net9.0 && zip -j ../../../../artifacts/JellyMusicDiscovery.zip JellyMusicDiscovery.dll TagLibSharp.dll meta.json
	@echo "Packaged: ../artifacts/JellyMusicDiscovery.zip"

clean:
	rm -rf bin obj ../artifacts
