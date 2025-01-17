##@ General

# The help target prints out all targets with their descriptions organized
# beneath their categories. The categories are represented by '##@' and the
# target descriptions by '##'. The awk commands is responsible for reading the
# entire set of makefiles included in this invocation, looking for lines of the
# file as xyz: ## something, and then pretty-format the target and help. Then,
# if there's a line with ##@ something, that gets pretty-printed as a category.
# More info on the usage of ANSI control characters for terminal formatting:
# https://en.wikipedia.org/wiki/ANSI_escape_code#SGR_parameters
# More info on the awk command:
# http://linuxcommand.org/lc3_adv_awk.php

IMAGE_NAME ?= playtron/steambus-builder
IMAGE_TAG ?= latest

PREFIX ?= $(HOME)/.local

VERSION := $(shell grep -E '^Version:' SteamBus.App/steam-bus.spec | awk '{print $$2}')

.PHONY: help
help: ## Display this help.
	@awk 'BEGIN {FS = ":.*##"; printf "\nUsage:\n  make \033[36m<target>\033[0m\n"} /^[a-zA-Z_0-9-]+:.*?##/ { printf "  \033[36m%-15s\033[0m %s\n", $$1, $$2 } /^##@/ { printf "\n\033[1m%s\033[0m\n", substr($$0, 5) } ' $(MAKEFILE_LIST)

.PHONY: restore
restore: ## Restores dependencies
	cd ./SteamBus.App && dotnet restore

.PHONY: build
build: ## Build the project
	cd ./SteamBus.App && dotnet build

.PHONY: build-release
build-release: ## Build the project in release mode
	cd ./SteamBus.App && dotnet publish -r linux-x64 -c Release -o ./build

.PHONY: rpm
rpm: ## Builds the RPM package
	mkdir -p /tmp/rpmbuild/BUILD
	mkdir -p /tmp/rpmbuild/RPMS
	mkdir -p /tmp/rpmbuild/SOURCES
	mkdir -p /tmp/rpmbuild/SPECS
	mkdir -p /tmp/rpmbuild/SRPMS
	cp ./SteamBus.App/steam-bus.spec /tmp/rpmbuild/SPECS/
	rm -rf /tmp/rpmbuild/SOURCES/SteamBus
	cp -r ./SteamBus.App/build /tmp/rpmbuild/SOURCES/SteamBus
	cp ./Makefile /tmp/rpmbuild/SOURCES/SteamBus/
	cp ./LICENSE /tmp/rpmbuild/SOURCES/SteamBus/
	cp ./README.md /tmp/rpmbuild/SOURCES/SteamBus/
	tar --transform 's/^build/SteamBus/' -czf ./SteamBus-$(VERSION).tar.gz -C ./SteamBus.App build
	rpmbuild --define "_topdir /tmp/rpmbuild" -bb /tmp/rpmbuild/SPECS/steam-bus.spec
	mv /tmp/rpmbuild/RPMS/x86_64/SteamBus-$(VERSION)-1.fc41.x86_64.rpm .

.PHONY: install
install: ## Performs install step for RPM
	# Create target directories
	mkdir -p $(PREFIX)/share/playtron/plugins/SteamBus
	mkdir -p $(PREFIX)/share/licenses/SteamBus
	mkdir -p $(PREFIX)/share/doc/SteamBus
	mkdir -p $(PREFIX)/bin

	# Copy files to th$(PREFIX)	cp -r $(SOURCE)/SteamBus/* $(PREFIX)/usr/share/playtron/plugins/SteamBus/
	cp $(SOURCE)/SteamBus/LICENSE $(PREFIX)/share/licenses/SteamBus/LICENSE
	cp $(SOURCE)/SteamBus/README.md $(PREFIX)/share/doc/SteamBus/README.md

	# Create a symlink for the executable
	ln -s ../share/playtron/plugins/SteamBus/SteamBus $(PREFIX)/bin/steam-bus

.PHONY: clean
clean: ## Remove build artifacts
	rm -rf ./SteamBus.Tests/obj/ ./SteamBus.Tests/bin/
	rm -rf ./SteamBus.App/bin/ ./SteamBus.App/obj/

.PHONY: run
run: ## Run the project
	cd ./SteamBus.App && dotnet run

.PHONY: test
test: ## Run project tests
	cd ./SteamBus.Tests && dotnet test

# Refer to .releaserc.yaml for release configuration
.PHONY: sem-release 
sem-release: ## Publish a release with semantic release 
	npx semantic-release

# E.g. make in-docker TARGET=build
.PHONY: in-docker
in-docker:
	@# Run the given make target inside Docker
	docker build -t $(IMAGE_NAME):$(IMAGE_TAG) .
	docker run --rm \
		-v $(PWD):/src:Z \
		--workdir /src \
		--user $(shell id -u):$(shell id -g) \
		-e DOTNET_CLI_HOME=/tmp \
		$(IMAGE_NAME):$(IMAGE_TAG) \
		make $(TARGET)
