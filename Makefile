ROOT=$(PWD)
APP_ROOT=$(ROOT)/src/YouTubeClipGenerator
APP_PROJECT=$(APP_ROOT)/YouTubeClipGenerator.csproj
BUILD_TYPE=Release
ARTIFACTS_DIR=$(ROOT)/artifacts

app_linux:
	rm -rf $(ARTIFACTS_DIR)/linux-x64
	dotnet build $(APP_PROJECT) -c $(BUILD_TYPE) -r linux-x64
	dotnet publish $(APP_PROJECT) -c $(BUILD_TYPE) -r linux-x64 -o $(ARTIFACTS_DIR)/linux-x64

app_macos_x64:
	rm -rf $(ARTIFACTS_DIR)/osx-x64
	dotnet build $(APP_PROJECT) -c $(BUILD_TYPE) -r osx-x64
	dotnet publish $(APP_PROJECT) -c $(BUILD_TYPE) -r osx-x64 -o $(ARTIFACTS_DIR)/osx-x64

app_macos_arm64:
	rm -rf $(ARTIFACTS_DIR)/osx-arm64
	dotnet build $(APP_PROJECT) -c $(BUILD_TYPE) -r osx-arm64
	dotnet publish $(APP_PROJECT) -c $(BUILD_TYPE) -r osx-arm64 -o $(ARTIFACTS_DIR)/osx-arm64

app_macos: app_macos_x64 app_macos_arm64

clean:
	rm -rf $(ARTIFACTS_DIR)