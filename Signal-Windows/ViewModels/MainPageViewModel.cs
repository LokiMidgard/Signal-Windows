using GalaSoft.MvvmLight;
using libsignalservice;
using libsignalservice.messages;
using libsignalservice.push.exceptions;
using libsignalservice.util;
using Microsoft.Extensions.Logging;
using Microsoft.Toolkit.Uwp.Notifications;
using Signal_Windows.Controls;
using Signal_Windows.Lib;
using Signal_Windows.Models;
using Signal_Windows.Storage;
using Signal_Windows.Views;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.ApplicationModel.Core;
using Windows.Networking.BackgroundTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using Windows.UI.Notifications;
using Windows.UI.ViewManagement;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using static libsignalservice.SignalServiceMessagePipe;

namespace Signal_Windows.ViewModels
{
    public partial class MainPageViewModel : ViewModelBase
    {
        private readonly ILogger Logger = LibsignalLogging.CreateLogger<MainPageViewModel>();
        public string RequestedConversationId;
        public SignalConversation SelectedThread;
        private Dictionary<string, SignalConversation> ConversationsDictionary = new Dictionary<string, SignalConversation>();
        public MainPage View;
        public ObservableCollection<SignalConversation> Conversations { get; set; } = new ObservableCollection<SignalConversation>();

        private Visibility _ThreadVisibility = Visibility.Collapsed;
        public Visibility ThreadVisibility
        {
            get { return _ThreadVisibility; }
            set { _ThreadVisibility = value; RaisePropertyChanged(nameof(ThreadVisibility)); }
        }

        private Visibility _WelcomeVisibility;
        public Visibility WelcomeVisibility
        {
            get { return _WelcomeVisibility; }
            set { _WelcomeVisibility = value; RaisePropertyChanged(nameof(WelcomeVisibility)); }
        }

        private SignalConversation _SelectedConversation = null;
        public SignalConversation SelectedConversation
        {
            get { return _SelectedConversation; }
            set
            {
                if (_SelectedConversation != value)
                {
                    _SelectedConversation = value;
                    RaisePropertyChanged(nameof(SelectedConversation));
                }
            }
        }

        internal void BackButton_Click(object sender, BackRequestedEventArgs e)
        {
            UnselectConversation();
            e.Handled = true;
        }

        internal void UnselectConversation()
        {
            SelectedThread = null;
            View.Thread.DisposeCurrentThread();
            ThreadVisibility = Visibility.Collapsed;
            WelcomeVisibility = Visibility.Visible;
            View.SwitchToStyle(View.GetCurrentViewStyle());
            Utils.DisableBackButton(BackButton_Click);
        }

        private bool _ThreadListAlignRight;

        public bool ThreadListAlignRight
        {
            get { return _ThreadListAlignRight; }
            set { _ThreadListAlignRight = value; RaisePropertyChanged(nameof(ThreadListAlignRight)); }
        }

        private async Task<bool> SendMessage(string messageText)
        {
            Debug.WriteLine("starting sendmessage");
            try
            {
                if (string.IsNullOrEmpty(messageText))
                {
                    var filePicker = new FileOpenPicker();
                    // Without this the file picker throws an exception, this is not documented
                    filePicker.FileTypeFilter.Add("*");
                    StorageFile file = await filePicker.PickSingleFileAsync();
                    if (file != null)
                    {
                        // Setup the uploader
                        BackgroundUploader uploader = new BackgroundUploader();
                        uploader.Method = "PUT";
                        uploader.SetRequestHeader("Content-Type", "application/octet-stream");
                        uploader.SetRequestHeader("Connection", "close");
                        uploader.SuccessToastNotification = LibUtils.CreateToastNotification($"{file.Name} has finished uploading.");
                        uploader.FailureToastNotification = LibUtils.CreateToastNotification($"{file.Name} has failed to upload.");

                        // Then encrypt the attachment
                        byte[] attachmentKey = Util.getSecretBytes(64);
                        (byte[] digest, Stream encryptedData) encryptedAttachment;
                        long fileSize;
                        using (var stream = await file.OpenStreamForReadAsync())
                        {
                            fileSize = stream.Length;
                            encryptedAttachment = SignalLibHandle.Instance.EncryptAttachment(stream, attachmentKey);
                        }

                        // Save the enrypted data somewhere
                        StorageFile tempFile = await ApplicationData.Current.LocalFolder.CreateFileAsync($"{file.Name}.encrypted",
                            CreationCollisionOption.GenerateUniqueName);
                        using (var stream = await tempFile.OpenStreamForWriteAsync())
                        {
                            encryptedAttachment.encryptedData.CopyTo(stream);
                        }
                        encryptedAttachment.encryptedData.Dispose();

                        // Get the upload URL, we need it here so we can get the foreign ID
                        var uploadUrl = SignalLibHandle.Instance.RetrieveAttachmentUploadUrl();

                        // Create the message
                        SignalAttachment attachment = new SignalAttachment()
                        {
                            ContentType = file.ContentType,
                            Size = fileSize,
                            FileName = file.Name,
                            Status = SignalAttachmentStatus.InProgress,
                            Key = attachmentKey,
                            Digest = encryptedAttachment.digest,
                            StorageId = uploadUrl.id
                        };

                        // Create the upload
                        UploadOperation upload = await Task.Run(() =>
                        {
                            return uploader.CreateUpload(new Uri(uploadUrl.location), tempFile);
                        });

                        // Set the upload GUID so we can refer to it later
                        attachment.Guid = upload.Guid.ToString();

                        // Finish creating the message
                        List<SignalAttachment> attachments = new List<SignalAttachment>();
                        attachments.Add(attachment);
                        SignalMessage message = CreateMessage(string.Empty, attachments);

                        // Save the message to the DB
                        SignalDBContext.SaveMessageLocked(message);

                        // Start the attachment upload
                        await SignalLibHandle.Instance.HandleUpload(upload, true, message);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(messageText))
                {
                    SignalMessage message = CreateMessage(messageText);
                    await SignalLibHandle.Instance.SendMessage(message, SelectedThread);
                    Debug.WriteLine("keydown lock released");
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
                Debug.WriteLine(ex.Message);
                Debug.WriteLine(ex.StackTrace);
                return false;
            }
        }

        private SignalMessage CreateMessage(string messageText, List<SignalAttachment> attachments = null)
        {
            if (attachments == null)
            {
                attachments = new List<SignalAttachment>();
            }
            var now = Util.CurrentTimeMillis();
            return new SignalMessage()
            {
                Author = null,
                ComposedTimestamp = now,
                Content = new SignalMessageContent() { Content = messageText },
                ThreadId = SelectedThread.ThreadId,
                ReceivedTimestamp = now,
                Direction = SignalMessageDirection.Outgoing,
                Read = true,
                Type = SignalMessageType.Normal,
                Attachments = attachments,
                AttachmentsCount = (uint)attachments.Count
            };
        }

        internal void RepositionConversation(SignalConversation uiConversation)
        {
            for (int i = 0; i < Conversations.Count; i++)
            {
                var c = Conversations[i];
                if (c == uiConversation)
                {
                    break;
                }
                if (uiConversation.LastActiveTimestamp > c.LastActiveTimestamp)
                {
                    int index = Conversations.IndexOf(uiConversation);
                    Logger.LogDebug("RepositionConversation() moving conversation from {0} to {1}", index, i);
                    Conversations.Move(index, i);
                    break;
                }
            }
        }

        internal async Task SendMessageButton_Click(TextBox messageTextBox)
        {
            bool sendMessageResult = await SendMessage(messageTextBox.Text);
            if (sendMessageResult)
            {
                messageTextBox.Text = string.Empty;
            }
        }

        public void SelectConversation(string conversationId)
        {
            SelectedConversation = ConversationsDictionary[conversationId];
        }

        public void ConversationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.AddedItems.Count == 1)
            {
                Logger.LogDebug("ContactsList_SelectionChanged()");
                WelcomeVisibility = Visibility.Collapsed;
                ThreadVisibility = Visibility.Visible;
                SelectedThread = SelectedConversation;
                View.Thread.Load(SelectedThread);
                View.SwitchToStyle(View.GetCurrentViewStyle());
            }
        }

        #region SignalFrontend API
        public void AddOrUpdateConversation(SignalConversation conversation, SignalMessage updateMessage)
        {
            SignalConversation uiConversation;
            if (!ConversationsDictionary.ContainsKey(conversation.ThreadId))
            {
                uiConversation = conversation.Clone();
                Conversations.Add(uiConversation);
                ConversationsDictionary.Add(uiConversation.ThreadId, uiConversation);
            }
            else
            {
                uiConversation = ConversationsDictionary[conversation.ThreadId];
                uiConversation.LastActiveTimestamp = conversation.LastActiveTimestamp;
                uiConversation.CanReceive = conversation.CanReceive;
                uiConversation.LastMessage = conversation.LastMessage;
                uiConversation.LastSeenMessage = conversation.LastSeenMessage;
                uiConversation.LastSeenMessageIndex = conversation.LastSeenMessageIndex;
                uiConversation.MessagesCount = conversation.MessagesCount;
                uiConversation.ThreadDisplayName = conversation.ThreadDisplayName;
                uiConversation.UnreadCount = conversation.UnreadCount;
                if (uiConversation is SignalContact ourContact && conversation is SignalContact newContact)
                {
                    ourContact.Color = newContact.Color;
                }
                else if (uiConversation is SignalGroup ourGroup && conversation is SignalGroup newGroup)
                {
                    ourGroup.GroupMemberships = newGroup.GroupMemberships;
                }
                if (SelectedThread != null) // the conversation we have open may have been edited
                {
                    if (SelectedThread == uiConversation)
                    {
                        if (updateMessage != null)
                        {
                            var container = new SignalMessageContainer(updateMessage, (int)SelectedThread.MessagesCount - 1);
                            View.Thread.Append(container);
                            View.Reload();
                        }
                    }
                    else if (SelectedThread is SignalGroup selectedGroup)
                    {
                        if (selectedGroup.GroupMemberships.FindAll((gm) => gm.Contact.ThreadId == conversation.ThreadId).Count > 0) // A group member was edited
                        {
                            View.Reload();
                        }
                    }
                }
                uiConversation.UpdateUI?.Invoke();
            }
            RepositionConversation(uiConversation);
        }

        public async Task HandleMessage(SignalMessage message, SignalConversation conversation)
        {
            var localConversation = ConversationsDictionary[conversation.ThreadId];
            localConversation.LastMessage = message;
            localConversation.MessagesCount = conversation.MessagesCount;
            localConversation.LastActiveTimestamp = conversation.LastActiveTimestamp;
            localConversation.UnreadCount = conversation.UnreadCount;
            localConversation.LastSeenMessageIndex = conversation.LastSeenMessageIndex;
            localConversation.UpdateUI();
            if (SelectedThread != null && SelectedThread == localConversation)
            {
                var container = new SignalMessageContainer(message, (int)SelectedThread.MessagesCount - 1);
                View.Thread.Append(container);
            }
            RepositionConversation(localConversation);

            if (ApplicationView.GetForCurrentView().Id == App.MainViewId)
            {
                if (message.Author != null)
                {
                    SignalNotifications.TryVibrate(true);
                    SignalNotifications.SendMessageNotification(message);
                    SignalNotifications.SendTileNotification(message);
                }
            }
        }

        public async Task HandleIdentitykeyChange(LinkedList<SignalMessage> messages)
        {
            foreach(var message in messages)
            {
                var conversation = ConversationsDictionary[message.ThreadId];
                conversation.MessagesCount += 1;
                conversation.UnreadCount += 1;
                await HandleMessage(message, conversation);
            }
        }

        public void HandleMessageUpdate(SignalMessage updatedMessage)
        {
            if (SelectedThread != null && SelectedThread.ThreadId == updatedMessage.ThreadId)
            {
                View.Thread.UpdateMessageBox(updatedMessage);
            }
        }

        public void ReplaceConversationList(List<SignalConversation> conversations)
        {
            ConversationsDictionary.Clear();
            Conversations.Clear();
            Conversations.AddRange(conversations);

            foreach (var c in Conversations)
            {
                ConversationsDictionary.Add(c.ThreadId, c);
            }
            if (SelectedThread != null)
            {
                SelectedThread = ConversationsDictionary[SelectedThread.ThreadId];
                View.Thread.Collection.Conversation = SelectedThread;
            }

            if (RequestedConversationId != null && RequestedConversationId != "")
            {
                SelectConversation(RequestedConversationId);
            }
        }
        #endregion
    }
}