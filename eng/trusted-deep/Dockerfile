ARG BASE
FROM ${BASE}
USER root
RUN mkdir -p /runner /opt/mini /opt/trusted && chown 1001:1001 /runner
COPY controller.py admission.py checkout-sources.py portable-gate.sh policy.json sources.json /opt/mini/
COPY bundles /opt/mini/bundles
COPY run.sh job.py coverage_evidence.py manifest.py private_admission.py private-job.sh evidence.py raw-evidence.mjs host-baseline.mjs /opt/trusted/
RUN chmod -R a+rX,a-w /opt/mini /opt/trusted && chmod 555 /opt/mini/portable-gate.sh /opt/trusted/run.sh
USER 1001:1001
WORKDIR /runner
ENTRYPOINT ["timeout", "--kill-after=10s", "3300", "bash", "/opt/trusted/run.sh"]
