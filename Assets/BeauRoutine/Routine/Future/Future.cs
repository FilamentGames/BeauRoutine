/*
 * Copyright (C) 2016-2020. Autumn Beauchesne. All rights reserved.
 * Author:  Autumn Beauchesne
 * Date:    8 Oct 2017
 * 
 * File:    Future.cs
 * Purpose: Single-assignment Future value. Can be used to return values
            from a coroutine.
*/

using System;
using System.Collections;
using UnityEngine;

namespace BeauRoutine
{
    internal enum FutureState { InProgress, Completed, Failed, Cancelled }

    /// <summary>
    /// Static methods for creating Futures.
    /// </summary>
    public partial class Future : IFuture
    {
        #region Types

        /// <summary>
        /// Failure state for a future.
        /// </summary>
        public struct Failure
        {
            /// <summary>
            /// How a future has failed.
            /// </summary>
            public FailureType Type;

            /// <summary>
            /// Failure details.
            /// </summary>
            public object Object;

            public override string ToString()
            {
                return string.Format("[Failure {0} ({1})]", Type, Object);
            }
        }

        /// <summary>
        /// Common error codes for Fail calls.
        /// </summary>
        public enum FailureType
        {
            Unknown,

            Exception,
            RoutineStopped,
            NullReference
        }

        #endregion // Types

        #region Future

        private FutureState m_State;

        private float m_Progress = 0;
        private Action<float> m_CallbackProgress;

        private Action m_CallbackComplete;

        private Future.Failure m_Failure;
        private Action m_CallbackFail;
        private Action<Future.Failure> m_CallbackFailWithArgs;

        private Routine m_Prophet;
        private AsyncHandle m_Async;

        public Future()
        {
            m_State = FutureState.InProgress;
            m_CallbackComplete = null;
            m_CallbackFail = null;
        }

        public Future(Action inCompleteCallback)
        {
            m_State = FutureState.InProgress;
            m_CallbackComplete = inCompleteCallback;
            m_CallbackFail = null;
        }

        public Future(Action inCompleteCallback, Action inFailureCallback)
        {
            m_State = FutureState.InProgress;
            m_CallbackComplete = inCompleteCallback;
            m_CallbackFail = inFailureCallback;
        }

        public Future(Action inCompleteCallback, Action<Future.Failure> inFailureCallback)
        {
            m_State = FutureState.InProgress;
            m_CallbackComplete = inCompleteCallback;
            m_CallbackFailWithArgs = inFailureCallback;
        }

        /// <summary>
        /// Cancels the Future if not already completed or failed,
        /// and cleans up references.
        /// </summary>
        public void Dispose()
        {
            Cancel();

            m_Progress = 0;
            m_CallbackComplete = null;
            m_Failure.Object = null;
            m_Failure.Type = Future.FailureType.Unknown;
            m_CallbackFail = null;
            m_CallbackFailWithArgs = null;
            m_CallbackProgress = null;
        }

        /// <summary>
        /// Returns if the Future has been completed, failed, or cancelled.
        /// </summary>
        public bool IsDone() { return m_State != FutureState.InProgress; }

        #region Progress

        /// <summary>
        /// Returns if a Future is in progress.
        /// </summary>
        public bool IsInProgress() { return m_State == FutureState.InProgress; }

        /// <summary>
        /// Returns the future's progress towards completion. [0, 1]
        /// </summary>
        public float GetProgress()
        {
            return m_Progress;
        }

        /// <summary>
        /// Sets the future's progress towards completion. [0, 1]
        /// </summary>
        public void SetProgress(float inProgress)
        {
            if (m_State != FutureState.InProgress)
                return;

            inProgress = inProgress < 0 ? 0 : (inProgress > 1 ? 1 : inProgress);
            if (m_Progress != inProgress)
            {
                m_Progress = inProgress;
                if (m_CallbackProgress != null)
                    Async.InvokeAsync(m_CallbackProgress, m_Progress);
            }
        }

        IFuture IFuture.OnProgress(Action<float> inProgressCallback)
        {
            return OnProgress(inProgressCallback);
        }

        /// <summary>
        /// Adds a callback for when the Future progresses.
        /// </summary>
        public Future OnProgress(Action<float> inProgressCallback)
        {
            if (inProgressCallback == null)
                return this;

            if (m_State == FutureState.Completed)
                inProgressCallback(m_Progress);
            else if (m_State == FutureState.InProgress)
            {
                m_CallbackProgress += inProgressCallback;
                if (m_Progress > 0)
                    inProgressCallback(m_Progress);
            }

            return this;
        }

        #endregion

        #region Completion

        /// <summary>
        /// Returns if the Future has been completed.
        /// </summary>
        public bool IsComplete() { return m_State == FutureState.Completed; }

        /// <summary>
        /// Sets the future to complete, or throws an exception
        /// if the Future has already been completed or failed.
        /// </summary>
        public void Complete()
        {
            if (m_State == FutureState.Cancelled)
                return;

            if (m_State != FutureState.InProgress)
                throw new InvalidOperationException("Cannot set value of Future once Future has completed or failed!");
            m_State = FutureState.Completed;

            // Force progress to 1.
            if (m_Progress < 1)
            {
                m_Progress = 1;
                if (m_CallbackProgress != null)
                    Async.InvokeAsync(m_CallbackProgress, m_Progress);
                m_CallbackProgress = null;
            }

            if (m_CallbackComplete != null)
            {
                Async.InvokeAsync(m_CallbackComplete);
                m_CallbackComplete = null;
            }

            m_CallbackFail = null;
            m_CallbackFailWithArgs = null;
        }

        /// <summary>
        /// Adds a callback for when the Future is completed.
        /// Will call immediately if the Future has already completed.
        /// </summary>
        public Future OnComplete(Action inCallback)
        {
            if (inCallback == null)
                return this;

            if (m_State == FutureState.Completed)
                inCallback();
            else if (m_State == FutureState.InProgress)
                m_CallbackComplete += inCallback;

            return this;
        }

        #endregion

        #region Failure

        /// <summary>
        /// Returns if the Future has failed.
        /// </summary>
        public bool IsFailed() { return m_State == FutureState.Failed; }

        /// <summary>
        /// Returns the failure object, or throws an exception
        /// if the Future has not failed.
        /// </summary>
        public Future.Failure GetFailure()
        {
            if (m_State != FutureState.Failed)
                throw new InvalidOperationException("Cannot get error of Future before it has failed!");
            return m_Failure;
        }

        /// <summary>
        /// Attempts to return the failure object.
        /// </summary>
        public bool TryGetFailure(out Future.Failure outFailure)
        {
            outFailure = m_Failure;
            return m_State == FutureState.Failed;
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail()
        {
            Fail(Future.FailureType.Unknown, null);
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail(object inArg)
        {
            Fail(Future.FailureType.Unknown, inArg);
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail(Exception inException)
        {
            Fail(Future.FailureType.Exception, inException);
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail(Future.Failure inFailure)
        {
            Fail(inFailure.Type, inFailure.Object);
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail(Future.FailureType inType)
        {
            Fail(inType, null);
        }

        /// <summary>
        /// Fails the Future, or throws an exception
        /// if the Future has already been set or failed.
        /// </summary>
        public void Fail(Future.FailureType inType, object inArg)
        {
            if (m_State == FutureState.Cancelled)
                return;

            if (m_State != FutureState.InProgress)
                throw new InvalidOperationException("Cannot fail Future once Future has completed or failed!");
            m_State = FutureState.Failed;
            m_Failure.Type = inType;
            m_Failure.Object = inArg;

            if (m_CallbackFail != null)
            {
                Async.InvokeAsync(m_CallbackFail);
                m_CallbackFail = null;
            }

            if (m_CallbackFailWithArgs != null)
            {
                Async.InvokeAsync(InvokeFailure, m_CallbackFailWithArgs);
                m_CallbackFailWithArgs = null;
            }

            m_CallbackComplete = null;
            m_CallbackProgress = null;
        }

        IFuture IFuture.OnFail(Action inFailureCallback)
        {
            return OnFail(inFailureCallback);
        }

        /// <summary>
        /// Adds a callback for when the Future fails.
        /// Will call immediately if the Future has already failed.
        /// </summary>
        public Future OnFail(Action inCallback)
        {
            if (inCallback == null)
                return this;

            if (m_State == FutureState.Failed)
                inCallback();
            else if (m_State == FutureState.InProgress)
                m_CallbackFail += inCallback;

            return this;
        }

        IFuture IFuture.OnFail(Action<Future.Failure> inFailureCallback)
        {
            return OnFail(inFailureCallback);
        }

        /// <summary>
        /// Adds a callback for when the Future fails.
        /// Will call immediately if the Future has already failed.
        /// </summary>
        public Future OnFail(Action<Future.Failure> inCallback)
        {
            if (inCallback == null)
                return this;

            if (m_State == FutureState.Failed)
                inCallback(m_Failure);
            else if (m_State == FutureState.InProgress)
                m_CallbackFailWithArgs += inCallback;

            return this;
        }

        #endregion

        #region Cancellation

        /// <summary>
        /// Returns if the Future has been cancelled.
        /// </summary>
        public bool IsCancelled() { return m_State == FutureState.Cancelled; }

        /// <summary>
        /// Cancels the Future. It will no longer receive Complete or Fail calls.
        /// </summary>
        public void Cancel()
        {
            if (m_State == FutureState.InProgress)
            {
                m_State = FutureState.Cancelled;
                m_Prophet.Stop();
                m_Async.Cancel();
            }
        }

        #endregion

        IFuture IFuture.LinkTo(Routine inRoutine)
        {
            return LinkTo(inRoutine);
        }

        /// <summary>
        /// Links a Routine to the Future.
        /// If the Routine stops, the Future will fail.
        /// If the Future is cancelled, the Routine will Stop.
        /// </summary>
        public Future LinkTo(Routine inRoutine)
        {
            if (!m_Prophet && m_State == FutureState.InProgress)
            {
                m_Prophet = inRoutine;
                m_Prophet.OnStop(OnLinkedStopped);
            }
            return this;
        }

        /// <summary>
        /// Links an async handle to the Future.
        /// If the async operation stops, the Future will fail.
        /// If the Future is cancelled, the operation will Stop.
        /// </summary>
        public Future LinkTo(AsyncHandle inAsync)
        {
            if (m_Async == AsyncHandle.Null && m_State == FutureState.InProgress)
            {
                m_Async = inAsync;
                m_Async.OnStop(OnLinkedStopped);
            }
            return this;
        }

        /// <summary>
        /// Waits for the Future to be completed or failed.
        /// </summary>
        public IEnumerator Wait()
        {
            if (m_State == FutureState.InProgress)
                return WaitInternal();
            return null;
        }

        private IEnumerator WaitInternal()
        {
            while (m_State == FutureState.InProgress)
                yield return null;
        }

        private void OnLinkedStopped()
        {
            if (m_State == FutureState.InProgress)
                Fail(Future.FailureType.RoutineStopped);
        }

        private void InvokeFailure(Action<Future.Failure> inFailure)
        {
            inFailure(m_Failure);
        }

        public override string ToString()
        {
            string prophetName = m_Prophet.GetName();
            if (prophetName != null)
            {
                return string.Format("[Future; State={0}; Progress={1:0}%; LinkedTo={2}]", m_State, m_Progress * 100f, prophetName);
            }
            return string.Format("[Future; State={0}; Progress={1:0}%]", m_State, m_Progress * 100);
        }

        #endregion // Future
    }
}