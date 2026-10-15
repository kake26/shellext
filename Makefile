# Default .NET Makefile

CONFIG ?= Release
RID    ?= linux-x64
OUT    ?= ./publish
PROJ   ?= $(firstword $(wildcard *.csproj))

.DEFAULT_GOAL := help
.PHONY: help build build-self build-net build-single build-zip build-aot test watch run clean restore format

help:            ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-14s %s\n", $$1, $$2}'

restore:        ## dotnet restore
	dotnet restore $(PROJ)

build: restore  ## Build (default: Release, override with CONFIG=Debug)
	dotnet build -c $(CONFIG) --no-restore

build-self:     ## Self-contained publish
	dotnet publish -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false -o $(OUT)

build-net:      ## Framework-dependent publish
	dotnet publish -c $(CONFIG) --self-contained false /p:PublishAot=false -o $(OUT)

build-single:   ## Self-contained single-file publish
	dotnet publish -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false \
		/p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $(OUT)

build-zip:      ## Single-file with compression (smallest output)
	dotnet publish -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false \
		/p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true \
		/p:IncludeNativeLibrariesForSelfExtract=true -o $(OUT)

build-aot:      ## Native AOT publish
	dotnet publish -c $(CONFIG) -r $(RID) -o $(OUT) /p:PublishAot=true

test:           ## Run tests
	dotnet test -c $(CONFIG)

watch:          ## Hot reload dev loop
	dotnet watch run

run:            ## Run app (Debug)
	dotnet run -c Debug

format:         ## Format code
	dotnet format

clean:          ## Clean bin/obj and publish output
	dotnet clean; rm -rf $(OUT) bin obj
