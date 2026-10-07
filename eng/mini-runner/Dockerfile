# BASE must be an already reviewed Linux ARM64 toolchain image digest reference or locally verified tag (record its image ID).
ARG BASE
FROM ${BASE}
USER root
RUN id -u runner >/dev/null 2>&1 || useradd --uid 1001 --create-home runner
RUN mkdir -p /runner /opt/actions-runner /opt/mini && chown 1001:1001 /runner
COPY runner.tar.gz /tmp/runner.tar.gz
RUN echo '628b4a7258487b80c1d3c221095a7ded349f5ae0175fd3dfab708d07428f041b  /tmp/runner.tar.gz' | sha256sum -c - && tar -xzf /tmp/runner.tar.gz -C /opt/actions-runner && rm /tmp/runner.tar.gz
COPY admission.py controller.py finish-gate.py checkout-sources.py entrypoint.sh start-hook.sh portable-gate.sh policy.json sources.json /opt/mini/
COPY validators /opt/mini/validators
COPY bundles /opt/mini/bundles
RUN chmod -R a+rX,a-w /opt/mini /opt/actions-runner && chmod 555 /opt/mini/*.sh
ENV HOME=/runner/home ACTIONS_RUNNER_HOOK_JOB_STARTED=/opt/mini/start-hook.sh
USER 1001:1001
WORKDIR /runner
ENTRYPOINT ["/opt/mini/entrypoint.sh"]
