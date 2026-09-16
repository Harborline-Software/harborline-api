# Access replacement fixture

Regenerate from the authored initial pack with:

```sh
dotnet run --project tooling/conformance/access-replacement -c Release -- /absolute/path/to/harborline-api
```

This updates the signed 1.1.4 replacement and 1.1.4-atomicity-probe.0 artifacts
derived from the 1.1.3 seed, and the source/artifact hashes in their manifests.
The released 1.1.1 source and 1.1.2 artifacts remain immutable upgrade fixtures.
Update the literal hash and issuer pins in `T433AccessReplacementProducerTests` after regeneration.
The host tests compile the same generator source and require its output to equal the checked-in bytes.

The signing seed is explicitly public conformance material. Never use this key for a production
node or install it as a production trust root. Fixture tests trust this issuer only in their isolated
trust stores. Production export/signing code retains its normal unpredictable nonce; the fixture
signer fixes the timestamp and nonce only to make this test artifact reproducible.
