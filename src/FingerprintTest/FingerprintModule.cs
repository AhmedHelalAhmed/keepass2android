﻿using System;
using Android.Content;
using Javax.Crypto;
using Java.Security;
using Java.Lang;
using Android.Views.InputMethods;
using Android.App;
using Android.Hardware.Fingerprints;
using Android.OS;
using Android.Security.Keystore;
using Android.Preferences;
using Android.Util;
using Android.Widget;
using Java.IO;
using Java.Security.Cert;
using Javax.Crypto.Spec;

namespace keepass2android
{
	public class FingerprintModule
	{
		public Context Context { get; set; }

		public FingerprintModule (Context context)
		{
			Context = context;
		}

		public FingerprintManager FingerprintManager 
		{
			get { return (FingerprintManager) Context.GetSystemService(Context.FingerprintService); }
		}

		public KeyguardManager KeyguardManager 
		{
			get
			{
				return (KeyguardManager) Context.GetSystemService("keyguard");
			}
		}
	

		public KeyStore Keystore
		{
			get
			{
				try
				{
					return KeyStore.GetInstance("AndroidKeyStore");
				}
				catch (KeyStoreException e)
				{
					throw new RuntimeException("Failed to get an instance of KeyStore", e);
				}
			}
		}

		public KeyGenerator KeyGenerator
		{
			get
			{
				try
				{
					return KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore");
				}
				catch (NoSuchAlgorithmException e)
				{
					throw new RuntimeException("Failed to get an instance of KeyGenerator", e);
				}
				catch (NoSuchProviderException e)
				{
					throw new RuntimeException("Failed to get an instance of KeyGenerator", e);
				}
			}
		}

		public Cipher Cipher
		{
			get
			{
				try
				{
					return Cipher.GetInstance(KeyProperties.KeyAlgorithmAes + "/"
					                          + KeyProperties.BlockModeCbc + "/"
					                          + KeyProperties.EncryptionPaddingPkcs7);
				}
				catch (NoSuchAlgorithmException e)
				{
					throw new RuntimeException("Failed to get an instance of Cipher", e);
				}
				catch (NoSuchPaddingException e)
				{
					throw new RuntimeException("Failed to get an instance of Cipher", e);
				}
			}
		}

		public InputMethodManager InputMethodManager 
		{
			get { return (InputMethodManager) Context.GetSystemService(Context.InputMethodService); }
		}

		public ISharedPreferences SharedPreferences
		{
			get { return PreferenceManager.GetDefaultSharedPreferences(Context); }
		}
	}

	public abstract class FingerprintCrypt: FingerprintManager.AuthenticationCallback
	{
		protected const string FailedToInitCipher = "Failed to init Cipher";
		public override void OnAuthenticationError(FingerprintState errorCode, ICharSequence errString)
		{
			_callback.OnAuthenticationError(errorCode, errString);
		}

		public override void OnAuthenticationFailed()
		{
			if (!_selfCancelled)
				_callback.OnAuthenticationFailed();
		}

		public override void OnAuthenticationHelp(FingerprintState helpCode, ICharSequence helpString)
		{
			_callback.OnAuthenticationHelp(helpCode, helpString);
		}

		public override void OnAuthenticationSucceeded(FingerprintManager.AuthenticationResult result)
		{
			_callback.OnAuthenticationSucceeded(result);
		}

		protected readonly string _keyId;

		protected Cipher _cipher;
		private bool _selfCancelled;
		private CancellationSignal _cancellationSignal;
		protected FingerprintManager.CryptoObject _cryptoObject;
		private FingerprintManager.AuthenticationCallback _callback;
		protected KeyStore _keystore;
		
		private FingerprintManager _fingerprintManager;

		public FingerprintCrypt(FingerprintModule fingerprint, string keyId)
		{
			
			_keyId = keyId;
			
			_cipher = fingerprint.Cipher;
			_keystore = fingerprint.Keystore;
			
			_fingerprintManager = fingerprint.FingerprintManager;
			
		}

		public abstract bool InitCipher();
		protected static string GetAlias(string keyId)
		{
			return "keepass2android." + keyId;
		}
		protected static string GetIvPrefKey(string prefKey)
		{
			return prefKey + "_iv";
		}
		public bool IsFingerprintAuthAvailable
		{
			get
			{
				return _fingerprintManager.IsHardwareDetected
					&& _fingerprintManager.HasEnrolledFingerprints;
			}
		}

		public void StartListening(FingerprintManager.AuthenticationCallback callback)
		{
			if (!IsFingerprintAuthAvailable)
				return;

			_cancellationSignal = new CancellationSignal();
			_selfCancelled = false;
			_callback = callback;
			_fingerprintManager.Authenticate(_cryptoObject, _cancellationSignal, 0 /* flags */, this, null);
			
		}

		public void StopListening()
		{
			if (_cancellationSignal != null)
			{
				_selfCancelled = true;
				_cancellationSignal.Cancel();
				_cancellationSignal = null;
			}
		}

		public string Encrypt(string textToEncrypt)
		{
			return Base64.EncodeToString(_cipher.DoFinal(System.Text.Encoding.UTF8.GetBytes(textToEncrypt)), 0);
		}


		public void StoreEncrypted(string textToEncrypt, string prefKey, Context context)
		{
			var edit = PreferenceManager.GetDefaultSharedPreferences(context).Edit();

			edit.PutString(prefKey, Encrypt(textToEncrypt));
			edit.PutString(GetIvPrefKey(prefKey), Base64.EncodeToString(CipherIv,0));
			edit.Commit();
		}

		

		private byte[] CipherIv
		{
			get { return _cipher.GetIV(); }
		}
	}

	public class FingerprintDecryption : FingerprintCrypt
	{
		private readonly byte[] _iv;
		

		public FingerprintDecryption(FingerprintModule fingerprint, string keyId, byte[] iv) : base(fingerprint, keyId)
		{
			_iv = iv;
		}

		public FingerprintDecryption(FingerprintModule fingerprint, string keyId, Context context, string prefKey)
			: base(fingerprint, keyId)
		{
			_iv = Base64.Decode(PreferenceManager.GetDefaultSharedPreferences(context).GetString(GetIvPrefKey(prefKey), null), 0);
		}

		public override bool InitCipher()
		{
			
			try
			{
				_keystore.Load(null);
				var key = _keystore.GetKey(GetAlias(_keyId), null);
				var ivParams = new IvParameterSpec(_iv);
				_cipher.Init(CipherMode.DecryptMode, key, ivParams);

				_cryptoObject = new FingerprintManager.CryptoObject(_cipher);
				return true;
			}
			catch (KeyPermanentlyInvalidatedException)
			{
				return false;
			}
			catch (KeyStoreException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (CertificateException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (UnrecoverableKeyException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (IOException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (NoSuchAlgorithmException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (InvalidKeyException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
		}


		public string Decrypt(string encryted)
		{
			byte[] encryptedBytes = Base64.Decode(encryted, 0);
			return System.Text.Encoding.UTF8.GetString(_cipher.DoFinal(encryptedBytes));
		}

		public string DecryptStored(string prefKey, Context context)
		{
			string enc = PreferenceManager.GetDefaultSharedPreferences(context).GetString(prefKey, null);

			Toast.MakeText(context, "len="+Base64.Decode(enc, 0).Length.ToString(), ToastLength.Long).Show();
			return Decrypt(enc);
		}
	}

	public class FingerprintEncryption : FingerprintCrypt
	{
		
		private KeyGenerator _keyGen;
		

		public FingerprintEncryption(FingerprintModule fingerprint, string keyId) : 
			base(fingerprint, keyId)
		{
			_keyGen = fingerprint.KeyGenerator;
			CreateKey();
		}


		/// <summary>
		/// Creates a symmetric key in the Android Key Store which can only be used after the user 
		/// has authenticated with fingerprint.
		/// </summary>
		private void CreateKey()
		{
			try
			{
				_keystore.Load(null);
				_keyGen.Init(new KeyGenParameterSpec.Builder(GetAlias(_keyId),
					KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
					.SetBlockModes(KeyProperties.BlockModeCbc)
					// Require the user to authenticate with a fingerprint to authorize every use
					// of the key
					.SetUserAuthenticationRequired(true)
					.SetEncryptionPaddings(KeyProperties.EncryptionPaddingPkcs7)
					.Build());
				_keyGen.GenerateKey();
			}
			catch (NoSuchAlgorithmException e)
			{
				throw new RuntimeException(e);
			}
			catch (InvalidAlgorithmParameterException e)
			{
				throw new RuntimeException(e);
			}
			catch (CertificateException e)
			{
				throw new RuntimeException(e);
			}
			catch (IOException e)
			{
				throw new RuntimeException(e);
			}
		}

		public override bool InitCipher()
		{
			try
			{
				_keystore.Load(null);
				var key = _keystore.GetKey(GetAlias(_keyId), null);
				_cipher.Init(CipherMode.EncryptMode, key);

				_cryptoObject = new FingerprintManager.CryptoObject(_cipher);
				return true;
			}
			catch (KeyPermanentlyInvalidatedException)
			{
				return false;
			}
			catch (KeyStoreException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (CertificateException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (UnrecoverableKeyException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (IOException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (NoSuchAlgorithmException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
			catch (InvalidKeyException e)
			{
				throw new RuntimeException(FailedToInitCipher, e);
			}
		}
	}
}