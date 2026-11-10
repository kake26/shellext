# Default .NET Makefile

CONFIG ?= Release
RID    ?= linux-x64
OUT    ?= ./publish
PROJ   ?= $(firstword $(wildcard *.csproj))
TEST   ?= $(firstword $(wildcard *Tests/*.csproj))

.DEFAULT_GOAL := help
.PHONY: help build build-self build-net build-single build-zip build-aot check test watch run clean restore format

help:            ## Show available targets
	@grep -E '^[a-zA-Z_-]+:.*?## ' $(MAKEFILE_LIST) | awk 'BEGIN {FS = ":.*?## "}; {printf "  %-14s %s\n", $$1, $$2}'

restore:        ## dotnet restore
	dotnet restore $(PROJ)

build: restore  ## Build (default: Release, override with CONFIG=Debug)
	dotnet build $(PROJ) -c $(CONFIG) --no-restore

build-self:     ## Self-contained publish
	dotnet publish $(PROJ) -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false -o $(OUT)

build-net:      ## Framework-dependent publish
	dotnet publish $(PROJ) -c $(CONFIG) --self-contained false /p:PublishAot=false -o $(OUT)

build-single:   ## Self-contained single-file publish
	dotnet publish $(PROJ) -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false \
		/p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true -o $(OUT)

build-zip:      ## Single-file with compression (smallest output)
	dotnet publish $(PROJ) -c $(CONFIG) -r $(RID) --self-contained true /p:PublishAot=false \
		/p:PublishSingleFile=true /p:EnableCompressionInSingleFile=true \
		/p:IncludeNativeLibrariesForSelfExtract=true -o $(OUT)

build-aot:      ## Native AOT publish
	dotnet publish $(PROJ) -c $(CONFIG) -r $(RID) -o $(OUT) /p:PublishAot=true

check:          ## Fail if source files are not version-controlled
	@untracked=$$(fossil extras 2>/dev/null | grep -E '\.(cs|csproj|slnx?|md)$$' || true); \
	if [ -n "$$untracked" ]; then \
		echo "Untracked source files (fossil add them):"; \
		echo "$$untracked"; \
		exit 1; \
	fi

test: check     ## Run tests (auto-detected test project)
	@if [ -z "$(TEST)" ]; then echo "No *Tests/*.csproj found"; exit 1; fi
	dotnet test $(TEST) -c $(CONFIG)

watch:          ## Hot reload dev loop
	dotnet watch run --project $(PROJ)

run:            ## Run app (Debug)
	dotnet run --project $(PROJ) -c Debug

format:         ## Format code
	dotnet format

clean:          ## Clean bin/obj and publish output
	rm -rf $(OUT) bin obj