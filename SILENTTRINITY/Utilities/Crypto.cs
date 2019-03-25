﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Asn1.Sec;
using Org.BouncyCastle.Asn1.X9;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace SILENTTRINITY.Utilities
{
    public static class Crypto
    {
        public static byte[] KeyExchange(Uri url)
        {
            X9ECParameters x9EC = NistNamedCurves.GetByName("P-521");
            ECDomainParameters ecDomain = new ECDomainParameters(x9EC.Curve, x9EC.G, x9EC.N, x9EC.H, x9EC.GetSeed());
            AsymmetricCipherKeyPair aliceKeyPair = GenerateKeyPair( ecDomain);

            ECPublicKeyParameters alicePublicKey = (ECPublicKeyParameters)aliceKeyPair.Public;
            ECPublicKeyParameters bobPublicKey = GetBobPublicKey(url, x9EC, alicePublicKey);

            byte[] AESKey = GenerateAESKey(bobPublicKey, aliceKeyPair.Private);

            return AESKey;
        }

        static byte[] GenerateAESKey(ECPublicKeyParameters bobPublicKey, 
                                AsymmetricKeyParameter alicePrivateKey)
        {
            IBasicAgreement aKeyAgree = AgreementUtilities.GetBasicAgreement("ECDH");
            aKeyAgree.Init(alicePrivateKey);
            BigInteger sharedSecret = aKeyAgree.CalculateAgreement(bobPublicKey);
            byte[] sharedSecretBytes = sharedSecret.ToByteArrayUnsigned();

            IDigest digest = new Sha256Digest();
            byte[] symmetricKey = new byte[digest.GetDigestSize()];
            digest.BlockUpdate(sharedSecretBytes, 0, sharedSecretBytes.Length);
            digest.DoFinal(symmetricKey, 0);

            return symmetricKey;
        }

        static ECPublicKeyParameters GetBobPublicKey(Uri url, 
                                                            X9ECParameters x9EC,
                                                            ECPublicKeyParameters alicePublicKey)
        {
            KeyCoords bobCoords = GetBobCoords(url, alicePublicKey);
            var point = x9EC.Curve.CreatePoint(bobCoords.X, bobCoords.Y);
            return new ECPublicKeyParameters("ECDH", point, SecObjectIdentifiers.SecP521r1);
        }

        static AsymmetricCipherKeyPair GenerateKeyPair(ECDomainParameters ecDomain)
        {
            ECKeyPairGenerator g = (ECKeyPairGenerator)GeneratorUtilities.GetKeyPairGenerator("ECDH");
            g.Init(new ECKeyGenerationParameters(ecDomain, new SecureRandom()));

            AsymmetricCipherKeyPair aliceKeyPair = g.GenerateKeyPair();
            return aliceKeyPair;
        }

        static KeyCoords GetBobCoords(Uri url, ECPublicKeyParameters publicKey)
        {
            string json = GetJsonString(publicKey);

            string response = Encoding.UTF8.GetString(Http.Post(url, Encoding.UTF8.GetBytes(json)));
            response = response.Replace("\"", "'");

            Regex r = new Regex(@"': (.+?) ");
            var mcx = r.Matches(response);
            BigInteger x = new BigInteger(mcx[0].Value.Replace("': ", "").Replace(", ", ""));

            string mcy = response.Substring(response.LastIndexOf(": ", StringComparison.Ordinal) + 1).Replace("}","").Trim();
            BigInteger y = new BigInteger(mcy);

            
            return new KeyCoords { 
                X = x,
                Y = y
            };
        }


        public static Dictionary<string, string> ParseJSON(string s)
        {
            Regex r = new Regex("\"(?<Key>[\\w]*)\":\"?(?<Value>([\\s\\w\\d\\.\\\\\\-/:_\\+]+(,[,\\s\\w\\d\\.\\\\\\-/:_\\+]*)?)*)\"?");
            MatchCollection mc = r.Matches(s);

            Dictionary<string, string> json = new Dictionary<string, string>();

            foreach (Match k in mc)
            {
                json.Add(k.Groups["Key"].Value, k.Groups["Value"].Value);
            }
            return json;
        }

        static string GetJsonString(ECPublicKeyParameters publicKeyParameters)
        {
            string publicKeyJsonTemplate = @"{'x': X_VALUE, 'y': Y_VALUE}";
            string json = publicKeyJsonTemplate;
            json = json.Replace("X_VALUE", publicKeyParameters.Q.AffineXCoord.ToBigInteger().ToString());
            json = json.Replace("Y_VALUE", publicKeyParameters.Q.AffineYCoord.ToBigInteger().ToString());
            return json;
        }

        public static byte[] Decrypt(byte[] key, byte[] data)
        {
            byte[] decryptedData = default;

            byte[] iv = new byte[16];
            byte[] ciphertext = new byte[(data.Length - 32) - 16];
            byte[] hmac = new byte[32];

            Array.Copy(data, iv, 16);
            Array.Copy(data, data.Length - 32, hmac, 0, 32);
            Array.Copy(data, 16, ciphertext, 0, (data.Length - 32) - 16);

            using (HMACSHA256 hmacsha256 = new HMACSHA256(key))
            {
                byte[] computedHash = hmacsha256.ComputeHash(iv.Concat(ciphertext).ToArray());
                for (int i = 0; i < hmac.Length; i++)
                {
                    if (computedHash[i] != hmac[i])
                    {
                        return decryptedData;
                    }
                }
                decryptedData = AES.Decrypt(ciphertext, key, iv);
            }
            return decryptedData;
        }

        public static byte[] Encrypt(byte[] key, byte[] data)
        {
            IEnumerable<byte> blob = default(byte[]);

            using (RandomNumberGenerator rng = new RNGCryptoServiceProvider())
            {
                byte[] iv = new byte[16];
                rng.GetBytes(iv);

                byte[] encryptedData = AES.Encrypt(data, key, iv);

                using (HMACSHA256 hmacsha256 = new HMACSHA256(key))
                {
                    byte[] ivEncData = iv.Concat(encryptedData).ToArray();
                    byte[] hmac = hmacsha256.ComputeHash(ivEncData);
                    blob = ivEncData.Concat(hmac);
                }
            }
            return blob.ToArray();
        }
    }

    static class AES
    {
        public static byte[] Decrypt(byte[] data, byte[] key, byte[] iv)
        {
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Padding = PaddingMode.PKCS7;
                aesAlg.KeySize = 256;
                aesAlg.Key = key;
                aesAlg.IV = iv;

                ICryptoTransform decryptor = aesAlg.CreateDecryptor(aesAlg.Key, aesAlg.IV);

                using (MemoryStream decryptedData = new MemoryStream())
                {
                    using (CryptoStream cryptoStream = new CryptoStream(decryptedData, decryptor, CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                        return decryptedData.ToArray();
                    }
                }
            }
        }

        public static byte[] Encrypt(byte[] data, byte[] key, byte[] iv)
        {
            using (Aes aesAlg = Aes.Create())
            {
                aesAlg.Padding = PaddingMode.PKCS7;
                aesAlg.KeySize = 256;
                aesAlg.Key = key;
                aesAlg.IV = iv;

                ICryptoTransform decryptor = aesAlg.CreateEncryptor(aesAlg.Key, aesAlg.IV);

                using (MemoryStream encryptedData = new MemoryStream())
                {
                    using (CryptoStream cryptoStream = new CryptoStream(encryptedData, decryptor, CryptoStreamMode.Write))
                    {
                        cryptoStream.Write(data, 0, data.Length);
                        cryptoStream.FlushFinalBlock();
                        return encryptedData.ToArray();
                    }
                }
            }
        }
    }

    class KeyCoords
    {
        public BigInteger X { get; set; }
        public BigInteger Y { get; set; }
    }
}
