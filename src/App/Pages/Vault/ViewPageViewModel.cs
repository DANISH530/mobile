﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Bit.App.Abstractions;
using Bit.App.Resources;
using Bit.App.Utilities;
using Bit.Core;
using Bit.Core.Abstractions;
using Bit.Core.Enums;
using Bit.Core.Exceptions;
using Bit.Core.Models.View;
using Bit.Core.Utilities;
using Xamarin.CommunityToolkit.ObjectModel;
using Xamarin.Forms;

namespace Bit.App.Pages
{
    public class ViewPageViewModel : BaseViewModel
    {
        private readonly IDeviceActionService _deviceActionService;
        private readonly ICipherService _cipherService;
        private readonly IStateService _stateService;
        private readonly IPlatformUtilsService _platformUtilsService;
        private readonly IAuditService _auditService;
        private readonly IMessagingService _messagingService;
        private readonly IEventService _eventService;
        private readonly IPasswordRepromptService _passwordRepromptService;
        private readonly ILocalizeService _localizeService;
        private readonly IClipboardService _clipboardService;
        private readonly ILogger _logger;

        private CipherView _cipher;
        private List<ViewPageFieldViewModel> _fields;
        private bool _canAccessPremium;
        private bool _showPassword;
        private bool _showCardNumber;
        private bool _showCardCode;
        private string _totpCode;
        private string _totpCodeFormatted;
        private string _totpSec;
        private bool _totpLow;
        private DateTime? _totpInterval = null;
        private string _previousCipherId;
        private byte[] _attachmentData;
        private string _attachmentFilename;
        private bool _passwordReprompted;
        private TotpHelper _totpTickHelper;
        private CancellationTokenSource _totpTickCancellationToken;
        private Task _totpTickTask;

        public ViewPageViewModel()
        {
            _deviceActionService = ServiceContainer.Resolve<IDeviceActionService>("deviceActionService");
            _cipherService = ServiceContainer.Resolve<ICipherService>("cipherService");
            _stateService = ServiceContainer.Resolve<IStateService>("stateService");
            _platformUtilsService = ServiceContainer.Resolve<IPlatformUtilsService>("platformUtilsService");
            _auditService = ServiceContainer.Resolve<IAuditService>("auditService");
            _messagingService = ServiceContainer.Resolve<IMessagingService>("messagingService");
            _eventService = ServiceContainer.Resolve<IEventService>("eventService");
            _passwordRepromptService = ServiceContainer.Resolve<IPasswordRepromptService>("passwordRepromptService");
            _localizeService = ServiceContainer.Resolve<ILocalizeService>("localizeService");
            _clipboardService = ServiceContainer.Resolve<IClipboardService>("clipboardService");
            _logger = ServiceContainer.Resolve<ILogger>("logger");

            CopyCommand = new AsyncCommand<string>((id) => CopyAsync(id, null), onException: ex => _logger.Exception(ex), allowsMultipleExecutions: false);
            CopyUriCommand = new AsyncCommand<LoginUriView>(uriView => CopyAsync("LoginUri", uriView.Uri), onException: ex => _logger.Exception(ex), allowsMultipleExecutions: false);
            CopyFieldCommand = new AsyncCommand<FieldView>(field => CopyAsync(field.Type == FieldType.Hidden ? "H_FieldValue" : "FieldValue", field.Value), onException: ex => _logger.Exception(ex), allowsMultipleExecutions: false);
            LaunchUriCommand = new Command<LoginUriView>(LaunchUri);
            TogglePasswordCommand = new Command(TogglePassword);
            ToggleCardNumberCommand = new Command(ToggleCardNumber);
            ToggleCardCodeCommand = new Command(ToggleCardCode);
            CheckPasswordCommand = new Command(CheckPasswordAsync);
            DownloadAttachmentCommand = new Command<AttachmentView>(DownloadAttachmentAsync);

            PageTitle = AppResources.ViewItem;
        }

        public ICommand CopyCommand { get; set; }
        public ICommand CopyUriCommand { get; set; }
        public ICommand CopyFieldCommand { get; set; }
        public Command LaunchUriCommand { get; set; }
        public Command TogglePasswordCommand { get; set; }
        public Command ToggleCardNumberCommand { get; set; }
        public Command ToggleCardCodeCommand { get; set; }
        public Command CheckPasswordCommand { get; set; }
        public Command DownloadAttachmentCommand { get; set; }
        public string CipherId { get; set; }
        public CipherView Cipher
        {
            get => _cipher;
            set => SetProperty(ref _cipher, value,
                additionalPropertyNames: new string[]
                {
                    nameof(IsLogin),
                    nameof(IsIdentity),
                    nameof(IsCard),
                    nameof(IsSecureNote),
                    nameof(ShowUris),
                    nameof(ShowAttachments),
                    nameof(ShowTotp),
                    nameof(ColoredPassword),
                    nameof(UpdatedText),
                    nameof(PasswordUpdatedText),
                    nameof(PasswordHistoryText),
                    nameof(ShowIdentityAddress),
                    nameof(IsDeleted),
                    nameof(CanEdit),
                    nameof(ShowUpgradePremiumTotpText)
                });
        }
        public List<ViewPageFieldViewModel> Fields
        {
            get => _fields;
            set => SetProperty(ref _fields, value);
        }
        public bool CanAccessPremium
        {
            get => _canAccessPremium;
            set => SetProperty(ref _canAccessPremium, value);
        }
        public bool ShowPassword
        {
            get => _showPassword;
            set => SetProperty(ref _showPassword, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ShowPasswordIcon),
                    nameof(PasswordVisibilityAccessibilityText)
                });
        }
        public bool ShowCardNumber
        {
            get => _showCardNumber;
            set => SetProperty(ref _showCardNumber, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ShowCardNumberIcon)
                });
        }
        public bool ShowCardCode
        {
            get => _showCardCode;
            set => SetProperty(ref _showCardCode, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ShowCardCodeIcon)
                });
        }
        public bool IsLogin => Cipher?.Type == Core.Enums.CipherType.Login;
        public bool IsIdentity => Cipher?.Type == Core.Enums.CipherType.Identity;
        public bool IsCard => Cipher?.Type == Core.Enums.CipherType.Card;
        public bool IsSecureNote => Cipher?.Type == Core.Enums.CipherType.SecureNote;
        public FormattedString ColoredPassword => PasswordFormatter.FormatPassword(Cipher.Login.Password);
        public FormattedString UpdatedText
        {
            get
            {
                var fs = new FormattedString();
                fs.Spans.Add(new Span
                {
                    Text = string.Format("{0}:", AppResources.DateUpdated),
                    FontAttributes = FontAttributes.Bold
                });
                fs.Spans.Add(new Span
                {
                    Text = string.Format(" {0} {1}",
                        _localizeService.GetLocaleShortDate(Cipher.RevisionDate.ToLocalTime()),
                        _localizeService.GetLocaleShortTime(Cipher.RevisionDate.ToLocalTime()))
                });
                return fs;
            }
        }
        public FormattedString PasswordUpdatedText
        {
            get
            {
                var fs = new FormattedString();
                fs.Spans.Add(new Span
                {
                    Text = string.Format("{0}:", AppResources.DatePasswordUpdated),
                    FontAttributes = FontAttributes.Bold
                });
                fs.Spans.Add(new Span
                {
                    Text = string.Format(" {0} {1}",
                        _localizeService.GetLocaleShortDate(Cipher.PasswordRevisionDisplayDate?.ToLocalTime()),
                        _localizeService.GetLocaleShortTime(Cipher.PasswordRevisionDisplayDate?.ToLocalTime()))
                });
                return fs;
            }
        }
        public FormattedString PasswordHistoryText
        {
            get
            {
                var fs = new FormattedString();
                fs.Spans.Add(new Span
                {
                    Text = string.Format("{0}:", AppResources.PasswordHistory),
                    FontAttributes = FontAttributes.Bold
                });
                fs.Spans.Add(new Span
                {
                    Text = string.Format(" {0}", Cipher.PasswordHistory.Count.ToString()),
                    TextColor = ThemeManager.GetResourceColor("PrimaryColor")
                });
                return fs;
            }
        }

        public bool ShowUpgradePremiumTotpText => !CanAccessPremium && ShowTotp;
        public bool ShowUris => IsLogin && Cipher.Login.HasUris;
        public bool ShowIdentityAddress => IsIdentity && (
            !string.IsNullOrWhiteSpace(Cipher.Identity.Address1) ||
            !string.IsNullOrWhiteSpace(Cipher.Identity.City) ||
            !string.IsNullOrWhiteSpace(Cipher.Identity.Country));
        public bool ShowAttachments => Cipher.HasAttachments && (CanAccessPremium || Cipher.OrganizationId != null);
        public bool ShowTotp => IsLogin && !string.IsNullOrWhiteSpace(Cipher.Login.Totp);
        public string ShowPasswordIcon => ShowPassword ? BitwardenIcons.EyeSlash : BitwardenIcons.Eye;
        public string ShowCardNumberIcon => ShowCardNumber ? BitwardenIcons.EyeSlash : BitwardenIcons.Eye;
        public string ShowCardCodeIcon => ShowCardCode ? BitwardenIcons.EyeSlash : BitwardenIcons.Eye;
        public string PasswordVisibilityAccessibilityText => ShowPassword ? AppResources.PasswordIsVisibleTapToHide : AppResources.PasswordIsNotVisibleTapToShow;
        public string TotpCodeFormatted
        {
            get => _canAccessPremium ? _totpCodeFormatted : string.Empty;
            set => SetProperty(ref _totpCodeFormatted, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ShowTotp)
                });
        }
        public string TotpSec
        {
            get => _totpSec;
            set => SetProperty(ref _totpSec, value,
                additionalPropertyNames: new string[]
                {
                    nameof(TotpProgress)
                });
        }
        public bool TotpLow
        {
            get => _totpLow;
            set
            {
                SetProperty(ref _totpLow, value);
                Page.Resources["textTotp"] = ThemeManager.Resources()[value ? "text-danger" : "text-default"];
            }
        }
        public double TotpProgress => string.IsNullOrEmpty(TotpSec) ? 0 : double.Parse(TotpSec) * 100 / 30;
        public bool IsDeleted => Cipher.IsDeleted;
        public bool CanEdit => !Cipher.IsDeleted;

        public async Task<bool> LoadAsync(Action finishedLoadingAction = null)
        {
            var cipher = await _cipherService.GetAsync(CipherId);
            if (cipher == null)
            {
                finishedLoadingAction?.Invoke();
                return false;
            }
            Cipher = await cipher.DecryptAsync();
            CanAccessPremium = await _stateService.CanAccessPremiumAsync();
            Fields = Cipher.Fields?.Select(f => new ViewPageFieldViewModel(this, Cipher, f)).ToList();

            if (Cipher.Type == Core.Enums.CipherType.Login && !string.IsNullOrWhiteSpace(Cipher.Login.Totp) &&
                (Cipher.OrganizationUseTotp || CanAccessPremium))
            {
                _totpTickHelper = new TotpHelper(Cipher);
                _totpTickCancellationToken?.Cancel();
                _totpTickCancellationToken = new CancellationTokenSource();
                _totpTickTask = new TimerTask(_logger, StartCiphersTotpTick, _totpTickCancellationToken).RunPeriodic();
            }
            if (_previousCipherId != CipherId)
            {
                var task = _eventService.CollectAsync(Core.Enums.EventType.Cipher_ClientViewed, CipherId);
            }
            _previousCipherId = CipherId;
            finishedLoadingAction?.Invoke();
            return true;
        }

        private async void StartCiphersTotpTick()
        {
            try
            {
                await _totpTickHelper.GenerateNewTotpValues();
                TotpSec = _totpTickHelper.TotpSec;
                TotpCodeFormatted = _totpTickHelper.TotpCodeFormatted;
            }
            catch (Exception ex)
            {
                _logger.Exception(ex);
            }
        }

        public async Task StopCiphersTotpTick()
        {
            _totpTickCancellationToken?.Cancel();
            if (_totpTickTask != null)
            {
                await _totpTickTask;
            }
        }

        public async void TogglePassword()
        {
            if (!await PromptPasswordAsync())
            {
                return;
            }

            ShowPassword = !ShowPassword;
            if (ShowPassword)
            {
                var task = _eventService.CollectAsync(Core.Enums.EventType.Cipher_ClientToggledPasswordVisible, CipherId);
            }
        }

        public async void ToggleCardNumber()
        {
            if (!await PromptPasswordAsync())
            {
                return;
            }
            ShowCardNumber = !ShowCardNumber;
            if (ShowCardNumber)
            {
                var task = _eventService.CollectAsync(
                    Core.Enums.EventType.Cipher_ClientToggledCardNumberVisible, CipherId);
            }
        }

        public async void ToggleCardCode()
        {
            if (!await PromptPasswordAsync())
            {
                return;
            }
            ShowCardCode = !ShowCardCode;
            if (ShowCardCode)
            {
                var task = _eventService.CollectAsync(
                    Core.Enums.EventType.Cipher_ClientToggledCardCodeVisible, CipherId);
            }
        }

        public async Task<bool> DeleteAsync()
        {
            if (Xamarin.Essentials.Connectivity.NetworkAccess == Xamarin.Essentials.NetworkAccess.None)
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.InternetConnectionRequiredMessage,
                    AppResources.InternetConnectionRequiredTitle);
                return false;
            }
            var confirmed = await _platformUtilsService.ShowDialogAsync(
                Cipher.IsDeleted ? AppResources.DoYouReallyWantToPermanentlyDeleteCipher : AppResources.DoYouReallyWantToSoftDeleteCipher,
                null, AppResources.Yes, AppResources.Cancel);
            if (!confirmed)
            {
                return false;
            }
            try
            {
                await _deviceActionService.ShowLoadingAsync(Cipher.IsDeleted ? AppResources.Deleting : AppResources.SoftDeleting);
                if (Cipher.IsDeleted)
                {
                    await _cipherService.DeleteWithServerAsync(Cipher.Id);
                }
                else
                {
                    await _cipherService.SoftDeleteWithServerAsync(Cipher.Id);
                }
                await _deviceActionService.HideLoadingAsync();
                _platformUtilsService.ShowToast("success", null,
                    Cipher.IsDeleted ? AppResources.ItemDeleted : AppResources.ItemSoftDeleted);
                _messagingService.Send(Cipher.IsDeleted ? "deletedCipher" : "softDeletedCipher", Cipher);
                return true;
            }
            catch (ApiException e)
            {
                await _deviceActionService.HideLoadingAsync();
                if (e?.Error != null)
                {
                    await _platformUtilsService.ShowDialogAsync(e.Error.GetSingleMessage(),
                        AppResources.AnErrorHasOccurred);
                }
            }
            return false;
        }

        public async Task<bool> RestoreAsync()
        {
            if (!IsDeleted)
            {
                return false;
            }
            if (Xamarin.Essentials.Connectivity.NetworkAccess == Xamarin.Essentials.NetworkAccess.None)
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.InternetConnectionRequiredMessage,
                    AppResources.InternetConnectionRequiredTitle);
                return false;
            }
            var confirmed = await _platformUtilsService.ShowDialogAsync(AppResources.DoYouReallyWantToRestoreCipher,
                null, AppResources.Yes, AppResources.Cancel);
            if (!confirmed)
            {
                return false;
            }
            try
            {
                await _deviceActionService.ShowLoadingAsync(AppResources.Restoring);
                await _cipherService.RestoreWithServerAsync(Cipher.Id);
                await _deviceActionService.HideLoadingAsync();
                _platformUtilsService.ShowToast("success", null, AppResources.ItemRestored);
                _messagingService.Send("restoredCipher", Cipher);
                return true;
            }
            catch (ApiException e)
            {
                await _deviceActionService.HideLoadingAsync();
                if (e?.Error != null)
                {
                    await _platformUtilsService.ShowDialogAsync(e.Error.GetSingleMessage(),
                        AppResources.AnErrorHasOccurred);
                }
            }
            return false;
        }

        private async void CheckPasswordAsync()
        {
            if (!(Page as BaseContentPage).DoOnce())
            {
                return;
            }
            if (string.IsNullOrWhiteSpace(Cipher.Login?.Password))
            {
                return;
            }
            if (Xamarin.Essentials.Connectivity.NetworkAccess == Xamarin.Essentials.NetworkAccess.None)
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.InternetConnectionRequiredMessage,
                    AppResources.InternetConnectionRequiredTitle);
                return;
            }
            await _deviceActionService.ShowLoadingAsync(AppResources.CheckingPassword);
            var matches = await _auditService.PasswordLeakedAsync(Cipher.Login.Password);
            await _deviceActionService.HideLoadingAsync();
            if (matches > 0)
            {
                await _platformUtilsService.ShowDialogAsync(string.Format(AppResources.PasswordExposed,
                    matches.ToString("N0")));
            }
            else
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.PasswordSafe);
            }
        }

        private async void DownloadAttachmentAsync(AttachmentView attachment)
        {
            if (!(Page as BaseContentPage).DoOnce())
            {
                return;
            }
            if (Xamarin.Essentials.Connectivity.NetworkAccess == Xamarin.Essentials.NetworkAccess.None)
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.InternetConnectionRequiredMessage,
                    AppResources.InternetConnectionRequiredTitle);
                return;
            }
            if (Cipher.OrganizationId == null && !CanAccessPremium)
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.PremiumRequired);
                return;
            }
            if (attachment.FileSize >= 10485760) // 10 MB
            {
                var confirmed = await _platformUtilsService.ShowDialogAsync(
                    string.Format(AppResources.AttachmentLargeWarning, attachment.SizeName), null,
                    AppResources.Yes, AppResources.No);
                if (!confirmed)
                {
                    return;
                }
            }

            var canOpenFile = true;
            if (!_deviceActionService.CanOpenFile(attachment.FileName))
            {
                if (Device.RuntimePlatform == Device.iOS)
                {
                    // iOS is currently hardcoded to always return CanOpenFile == true, but should it ever return false
                    // for any reason we want to be sure to catch it here.
                    await _platformUtilsService.ShowDialogAsync(AppResources.UnableToOpenFile);
                    return;
                }

                canOpenFile = false;
            }

            if (!await PromptPasswordAsync())
            {
                return;
            }

            await _deviceActionService.ShowLoadingAsync(AppResources.Downloading);
            try
            {
                var data = await _cipherService.DownloadAndDecryptAttachmentAsync(Cipher.Id, attachment, Cipher.OrganizationId);
                await _deviceActionService.HideLoadingAsync();
                if (data == null)
                {
                    await _platformUtilsService.ShowDialogAsync(AppResources.UnableToDownloadFile);
                    return;
                }

                if (Device.RuntimePlatform == Device.Android)
                {
                    if (canOpenFile)
                    {
                        // We can open this attachment directly, so give the user the option to open or save
                        PromptOpenOrSave(data, attachment);
                    }
                    else
                    {
                        // We can't open this attachment so go directly to save
                        SaveAttachment(data, attachment);
                    }
                }
                else
                {
                    OpenAttachment(data, attachment);
                }
            }
            catch
            {
                await _deviceActionService.HideLoadingAsync();
            }
        }

        public async void PromptOpenOrSave(byte[] data, AttachmentView attachment)
        {
            var selection = await Page.DisplayActionSheet(attachment.FileName, AppResources.Cancel, null,
                AppResources.Open, AppResources.Save);
            if (selection == AppResources.Open)
            {
                OpenAttachment(data, attachment);
            }
            else if (selection == AppResources.Save)
            {
                SaveAttachment(data, attachment);
            }
        }

        public async void OpenAttachment(byte[] data, AttachmentView attachment)
        {
            if (!_deviceActionService.OpenFile(data, attachment.Id, attachment.FileName))
            {
                await _platformUtilsService.ShowDialogAsync(AppResources.UnableToOpenFile);
                return;
            }
        }

        public async void SaveAttachment(byte[] data, AttachmentView attachment)
        {
            _attachmentData = data;
            _attachmentFilename = attachment.FileName;
            if (!_deviceActionService.SaveFile(_attachmentData, null, _attachmentFilename, null))
            {
                ClearAttachmentData();
                await _platformUtilsService.ShowDialogAsync(AppResources.UnableToSaveAttachment);
            }
        }

        public async void SaveFileSelected(string contentUri, string filename)
        {
            if (_deviceActionService.SaveFile(_attachmentData, null, filename ?? _attachmentFilename, contentUri))
            {
                ClearAttachmentData();
                _platformUtilsService.ShowToast("success", null, AppResources.SaveAttachmentSuccess);
                return;
            }

            ClearAttachmentData();
            await _platformUtilsService.ShowDialogAsync(AppResources.UnableToSaveAttachment);
        }

        private void ClearAttachmentData()
        {
            _attachmentData = null;
            _attachmentFilename = null;
        }

        private async Task CopyAsync(string id, string text = null)
        {
            if (_passwordRepromptService.ProtectedFields.Contains(id) && !await PromptPasswordAsync())
            {
                return;
            }

            string name = null;
            if (id == "LoginUsername")
            {
                text = Cipher.Login.Username;
                name = AppResources.Username;
            }
            else if (id == "LoginPassword")
            {
                text = Cipher.Login.Password;
                name = AppResources.Password;
            }
            else if (id == "LoginTotp")
            {
                text = TotpCodeFormatted.Replace(" ", string.Empty);
                name = AppResources.VerificationCodeTotp;
            }
            else if (id == "LoginUri")
            {
                name = AppResources.URI;
            }
            else if (id == "FieldValue" || id == "H_FieldValue")
            {
                name = AppResources.Value;
            }
            else if (id == "CardNumber")
            {
                text = Cipher.Card.Number;
                name = AppResources.Number;
            }
            else if (id == "CardCode")
            {
                text = Cipher.Card.Code;
                name = AppResources.SecurityCode;
            }

            if (text != null)
            {
                await _clipboardService.CopyTextAsync(text);
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _platformUtilsService.ShowToastForCopiedValue(name);
                }
                if (id == "LoginPassword")
                {
                    await _eventService.CollectAsync(Core.Enums.EventType.Cipher_ClientCopiedPassword, CipherId);
                }
                else if (id == "CardCode")
                {
                    await _eventService.CollectAsync(Core.Enums.EventType.Cipher_ClientCopiedCardCode, CipherId);
                }
                else if (id == "H_FieldValue")
                {
                    await _eventService.CollectAsync(Core.Enums.EventType.Cipher_ClientCopiedHiddenField, CipherId);
                }
            }
        }

        private void LaunchUri(LoginUriView uri)
        {
            if (uri.CanLaunch && (Page as BaseContentPage).DoOnce())
            {
                _platformUtilsService.LaunchUri(uri.LaunchUri);
            }
        }

        internal async Task<bool> PromptPasswordAsync()
        {
            if (Cipher.Reprompt == CipherRepromptType.None || _passwordReprompted)
            {
                return true;
            }

            return _passwordReprompted = await _passwordRepromptService.ShowPasswordPromptAsync();
        }
    }

    public class ViewPageFieldViewModel : ExtendedViewModel
    {
        private II18nService _i18nService;
        private ViewPageViewModel _vm;
        private FieldView _field;
        private CipherView _cipher;
        private bool _showHiddenValue;

        public ViewPageFieldViewModel(ViewPageViewModel vm, CipherView cipher, FieldView field)
        {
            _i18nService = ServiceContainer.Resolve<II18nService>("i18nService");
            _vm = vm;
            _cipher = cipher;
            Field = field;
            ToggleHiddenValueCommand = new Command(ToggleHiddenValue);
        }

        public FieldView Field
        {
            get => _field;
            set => SetProperty(ref _field, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ValueText),
                    nameof(IsBooleanType),
                    nameof(IsHiddenType),
                    nameof(IsTextType),
                    nameof(ShowCopyButton),
                });
        }

        public bool ShowHiddenValue
        {
            get => _showHiddenValue;
            set => SetProperty(ref _showHiddenValue, value,
                additionalPropertyNames: new string[]
                {
                    nameof(ShowHiddenValueIcon)
                });
        }

        public string ValueText
        {
            get
            {
                if (IsBooleanType)
                {
                    return _field.Value == "true" ? BitwardenIcons.CheckSquare : BitwardenIcons.Square;
                }
                else if (IsLinkedType)
                {
                    var i18nKey = _cipher.LinkedFieldI18nKey(Field.LinkedId.GetValueOrDefault());
                    return BitwardenIcons.Link + _i18nService.T(i18nKey);
                }
                else
                {
                    return _field.Value;
                }
            }
        }

        public FormattedString ColoredHiddenValue => PasswordFormatter.FormatPassword(_field.Value);

        public Command ToggleHiddenValueCommand { get; set; }

        public string ShowHiddenValueIcon => _showHiddenValue ? BitwardenIcons.EyeSlash : BitwardenIcons.Eye;
        public bool IsTextType => _field.Type == Core.Enums.FieldType.Text;
        public bool IsBooleanType => _field.Type == Core.Enums.FieldType.Boolean;
        public bool IsHiddenType => _field.Type == Core.Enums.FieldType.Hidden;
        public bool IsLinkedType => _field.Type == Core.Enums.FieldType.Linked;
        public bool ShowViewHidden => IsHiddenType && _cipher.ViewPassword;
        public bool ShowCopyButton => _field.Type != Core.Enums.FieldType.Boolean &&
            !string.IsNullOrWhiteSpace(_field.Value) &&
            !(IsHiddenType && !_cipher.ViewPassword) &&
            _field.Type != FieldType.Linked;

        public async void ToggleHiddenValue()
        {
            if (!await _vm.PromptPasswordAsync())
            {
                return;
            }
            ShowHiddenValue = !ShowHiddenValue;
            if (ShowHiddenValue)
            {
                var eventService = ServiceContainer.Resolve<IEventService>("eventService");
                var task = eventService.CollectAsync(
                    Core.Enums.EventType.Cipher_ClientToggledHiddenFieldVisible, _cipher.Id);
            }
        }
    }
}
