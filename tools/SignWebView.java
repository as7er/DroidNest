import java.nio.file.*;
import java.security.*;
import java.security.cert.*;
import java.security.spec.PKCS8EncodedKeySpec;
import java.util.*;
import com.android.apksig.ApkSigner;
import com.android.apksig.util.*;

// Public AOSP test key ONLY for this test-key-signed development image.
// Stream output to caller; never change the original downloaded artifact.
class SignWebView {
    public static void main(String[] args) throws Exception {
        PrivateKey key = KeyFactory.getInstance("RSA").generatePrivate(new PKCS8EncodedKeySpec(Files.readAllBytes(Paths.get(args[1]))));
        X509Certificate cert;
        try (java.io.InputStream in = Files.newInputStream(Paths.get(args[2]))) {
            cert = (X509Certificate) CertificateFactory.getInstance("X.509").generateCertificate(in);
        }
        ApkSigner.SignerConfig signer = new ApkSigner.SignerConfig.Builder("android", key, Collections.singletonList(cert)).build();
        ReadableDataSink output = DataSinks.newInMemoryDataSink();
        new ApkSigner.Builder(Collections.singletonList(signer)).setInputApk(new java.io.File(args[0]))
            .setOutputApk(output).setV4SigningEnabled(false).build().sign();
        output.feed(0, output.size(), DataSinks.asDataSink(System.out));
        System.out.flush();
    }
}
