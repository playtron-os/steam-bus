# Version 1.0.0

FROM fedora:41

RUN dnf update -y && \
    dnf install -y \
    rpm-build \
    rpmdevtools \
    dotnet-sdk-8.0 \
    tar \
    make \
    git \
    wget \
    nano \
    && dnf clean all

RUN rpmdev-setuptree

ENV RPMBUILD=/tmp/rpmbuild
