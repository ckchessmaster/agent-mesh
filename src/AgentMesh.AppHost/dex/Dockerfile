FROM ghcr.io/dexidp/dex:v2.41.1
USER root
RUN mkdir -p /var/dex && chown -R 1001:1001 /var/dex
USER 1001:1001
COPY config.yaml /etc/dex/cfg/config.yaml
CMD ["dex", "serve", "/etc/dex/cfg/config.yaml"]
