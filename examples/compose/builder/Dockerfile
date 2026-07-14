# Minimal multi-stage Dockerfile to demonstrate build: support (context, args, target).
FROM busybox:1.36 AS base
ARG GREETING=hello
RUN echo "$GREETING" > /greeting.txt

FROM base AS run
CMD ["sh", "-c", "cat /greeting.txt && while true; do sleep 30; done"]
