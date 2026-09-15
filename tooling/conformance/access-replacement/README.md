# Access replacement fixture

Regenerate from the authored initial pack with:

```sh
dotnet run --project tooling/conformance/access-replacement -c Release -- /absolute/path/to/harborline-api
```

This updates the signed 1.1.2 artifact and the source/artifact hashes in its manifest.
Update the literal hash and issuer pins in `T433AccessReplacementProducerTests` after regeneration.
The host tests compile the same generator source and require its output to equal the checked-in bytes.

The signing seed is explicitly public conformance material. Never use this key for a production
node or install it as a production trust root. Fixture tests trust this issuer only in their isolated
trust stores. Production export/signing code retains its normal unpredictable nonce; the fixture
signer fixes the timestamp and nonce only to make this test artifact reproducible.
