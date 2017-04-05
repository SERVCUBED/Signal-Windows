﻿using libsignalservice.crypto;
using libsignalservice.messages;
using libsignalservice.push;
using libsignalservice.util;
using Nito.AsyncEx;
using Signal_Windows.Models;
using Strilanc.Value;
using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Signal_Windows.ViewModels
{
    public partial class MainPageViewModel
    {
        /// <summary>
        /// ResetEvent that indicates the end of the pending db transactions
        /// </summary>
        private AsyncManualResetEvent MessageSavePendingSwitch = new AsyncManualResetEvent(false);

        /// <summary>
        /// Reads, decrypts, handles and schedules storing and displaying of incoming messages from the pipe
        /// </summary>
        public void HandleIncomingMessages()
        {
            Debug.WriteLine("HandleIncomingMessages starting...");
            try
            {
                while (Running)
                {
                    SignalManager.ReceiveBatch(this);
                }
            }
            catch (Exception) { }
            IncomingOffSwitch.Set();
            Debug.WriteLine("HandleIncomingMessages finished");
        }

        /// <summary>
        /// onMessages is called from the pipe after it received messages
        /// </summary>
        /// <param name="envelopes"></param>
        public void onMessages(SignalServiceEnvelope[] envelopes)
        {
            List<SignalMessage> messages = new List<SignalMessage>();
            foreach (var envelope in envelopes)
            {
                try
                {
                    var cipher = new SignalServiceCipher(new SignalServiceAddress((string)LocalSettings.Values["Username"]), SignalManager.SignalStore);
                    var content = cipher.decrypt(envelope);

                    //TODO handle special messages & unknown groups
                    if (content.getDataMessage().HasValue)
                    {
                        SignalServiceDataMessage message = content.getDataMessage().ForceGetValue();
                        if (message.isEndSession())
                        {
                            SignalManager.SignalStore.DeleteAllSessions(envelope.getSource());
                            SignalManager.Save();
                        }
                        else
                        {
                            //TODO check both the db and the previous messages for duplicates
                            messages.Add(HandleMessage(envelope, content, message));
                        }
                    }
                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                }
                finally
                {
                    SignalManager.Save();
                }
            }
            if (messages.Count > 0)
            {
                MessageSavePendingSwitch.Reset();
                Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, () =>
                {
                    UIHandleIncomingMessages(messages.ToArray());
                }).AsTask().Wait();
                MessageSavePendingSwitch.Wait(CancelSource.Token);
            }
        }

        private SignalMessage HandleMessage(SignalServiceEnvelope envelope, SignalServiceContent content, SignalServiceDataMessage dataMessage)
        {
            string source = envelope.getSource();
            SignalContact author = GetOrCreateContact(source);
            string body = dataMessage.getBody().HasValue ? dataMessage.getBody().ForceGetValue() : "";
            string threadId = dataMessage.getGroupInfo().HasValue ? Base64.encodeBytes(dataMessage.getGroupInfo().ForceGetValue().getGroupId()) : source;
            List<SignalAttachment> attachments = new List<SignalAttachment>();
            SignalMessage message = new SignalMessage()
            {
                Type = source == (string)LocalSettings.Values["Username"] ? (uint)SignalMessageType.Outgoing : (uint)SignalMessageType.Incoming,
                Status = (uint)SignalMessageStatus.Default,
                Author = author,
                Content = body,
                ThreadID = source,
                AuthorUsername = source,
                DeviceId = (uint)envelope.getSourceDevice(),
                Receipts = 0,
                ComposedTimestamp = envelope.getTimestamp(),
                ReceivedTimestamp = Util.CurrentTimeMillis(),
                AttachmentsCount = (uint)attachments.Count,
                Attachments = attachments
            };
            if (dataMessage.getAttachments().HasValue)
            {
                var receivedAttachments = dataMessage.getAttachments().ForceGetValue();
                foreach (var receivedAttachment in receivedAttachments)
                {
                    var pointer = receivedAttachment.asPointer();
                    SignalAttachment sa = new SignalAttachment()
                    {
                        Message = message,
                        Status = (uint)SignalAttachmentStatus.Default,
                        ContentType = "",
                        Key = pointer.getKey(),
                        Relay = pointer.getRelay(),
                        StorageId = pointer.getId()
                    };
                    attachments.Add(sa);
                }
            }
            Debug.WriteLine("received message: " + message.Content);
            return message;
        }
    }
}