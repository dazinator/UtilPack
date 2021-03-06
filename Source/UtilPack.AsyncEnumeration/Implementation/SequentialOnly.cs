﻿/*
 * Copyright 2017 Stanislav Muhametsin. All rights Reserved.
 *
 * Licensed  under the  Apache License,  Version 2.0  (the "License");
 * you may not use  this file  except in  compliance with the License.
 * You may obtain a copy of the License at
 *
 *   http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed  under the  License is distributed on an "AS IS" BASIS,
 * WITHOUT  WARRANTIES OR CONDITIONS  OF ANY KIND, either  express  or
 * implied.
 *
 * See the License for the specific language governing permissions and
 * limitations under the License. 
 */
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack;

using TAsyncPotentialToken = System.Nullable<System.Int64>;
using TAsyncToken = System.Int64;
using TSequentialCurrentInfoFactory = System.Func<System.Object, UtilPack.AsyncEnumeration.ResetAsyncDelegate, System.Object, System.Object>;

namespace UtilPack.AsyncEnumeration
{


   internal abstract class AsyncSequentialOnlyEnumeratorImpl<T> : AsyncEnumerator<T>
   {


      private const Int32 STATE_INITIAL = 0;
      private const Int32 MOVE_NEXT_STARTED = 1;
      private const Int32 MOVE_NEXT_ENDED = 2;
      private const Int32 STATE_ENDED = 3;
      private const Int32 RESETTING = 4;

      private Int32 _state;
      private SequentialEnumeratorCurrentInfo<T> _current;
      private readonly InitialMoveNextAsyncDelegate<T> _initialMoveNext;
      private readonly TSequentialCurrentInfoFactory _currentFactory;

      public AsyncSequentialOnlyEnumeratorImpl(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory
         )
      {
         this._state = STATE_INITIAL;
         this._current = null;
         this._initialMoveNext = ArgumentValidator.ValidateNotNull( nameof( initialMoveNext ), initialMoveNext );
         this._currentFactory = currentFactory;
      }

      public Boolean IsParallelEnumerationSupported => false;

      public async ValueTask<TAsyncPotentialToken> MoveNextAsync( CancellationToken token )
      {
         // We can call move next only in initial state, or after we have called it once
         Boolean success = false;
         var wasNotInitial = Interlocked.CompareExchange( ref this._state, MOVE_NEXT_STARTED, MOVE_NEXT_ENDED ) == MOVE_NEXT_ENDED;
         TAsyncPotentialToken retVal = null;
         if ( wasNotInitial || Interlocked.CompareExchange( ref this._state, MOVE_NEXT_STARTED, STATE_INITIAL ) == STATE_INITIAL )
         {
            ResetAsyncDelegate disposeDelegate = null;

            try
            {

               if ( wasNotInitial )
               {
                  var moveNext = this._current.MoveNext;
                  if ( moveNext == null )
                  {
                     success = false;
                  }
                  else
                  {
                     T current;
                     (success, current) = await moveNext( token );
                     if ( success )
                     {
                        this._current.Current = current;
                     }
                  }
               }
               else
               {
                  // First time calling move next
                  var result = await this.CallInitialMoveNext( token, this._initialMoveNext );
                  success = result.Item1;
                  if ( success )
                  {
                     Interlocked.Exchange( ref this._current, (SequentialEnumeratorCurrentInfo<T>) this._currentFactory?.Invoke( result.Item3, result.Item4, result.Item2 ) ?? new SequentialEnumeratorCurrentInfoWithObject<T>( result.Item3, result.Item4, result.Item2 ) );
                  }
                  else
                  {
                     disposeDelegate = result.Item4;
                  }
               }
            }
            finally
            {
               try
               {
                  if ( success )
                  {
                     var t = this.AfterMoveNextSucessful( this._current.Current );
                     if ( t != null )
                     {
                        await t;
                     }
                  }
                  else
                  {
                     await this.PerformDispose( token, disposeDelegate );
                  }
               }
               catch
               {
                  // Ignore.
               }

               if ( success )
               {
                  retVal = Interlocked.Increment( ref this._current._token );
               }
               else
               {
                  Interlocked.Exchange( ref this._current, null );
               }
               Interlocked.Exchange( ref this._state, success ? MOVE_NEXT_ENDED : STATE_ENDED );
            }
         }
         else if ( this._state != STATE_ENDED )
         {
            // Re-entrancy or concurrent with Reset -> exception
            // TODO -> Maybe use await + Interlocked.CompareExchange-loop to wait... ? Waiting is always prone to deadlocks though.
            throw new InvalidOperationException( "Tried to concurrently move to next or reset." );
         }

         return retVal;
      }

      public T OneTimeRetrieve( TAsyncToken retrievalToken )
      {
         SequentialEnumeratorCurrentInfo<T> cur;
         var success = ( cur = this._current ) != null && cur._token == retrievalToken;
         return success ? cur.Current : default;
      }

      public async ValueTask<Boolean> TryResetAsync( CancellationToken token )
      {
         // We can reset from MOVE_NEXT_STARTED and STATE_ENDED states
         var retVal = false;
         if (
            Interlocked.CompareExchange( ref this._state, RESETTING, MOVE_NEXT_STARTED ) == MOVE_NEXT_STARTED
            || Interlocked.CompareExchange( ref this._state, RESETTING, STATE_ENDED ) == STATE_ENDED
            )
         {
            try
            {
               var moveNext = this._current?.MoveNext;
               if ( moveNext != null )
               {
                  while ( ( await moveNext( token ) ).Item1 ) ;
               }
            }
            finally
            {
               try
               {
                  await this.PerformDispose( token );
               }
               catch
               {
                  // Ignore
               }

               Interlocked.Exchange( ref this._state, STATE_INITIAL );
               retVal = true;
            }
         }
         //else if ( this._state != STATE_INITIAL )
         //{
         //   // Re-entrancy or concurrent with move next -> exception
         //   throw new InvalidOperationException( "Tried to concurrently reset or move to next." );
         //}

         return retVal;
      }

      protected virtual async ValueTask<Boolean> PerformDispose( CancellationToken token, ResetAsyncDelegate disposeDelegate = null )
      {
         var prev = Interlocked.Exchange( ref this._current, null );
         var retVal = false;
         if ( prev != null || disposeDelegate != null )
         {
            if ( disposeDelegate == null )
            {
               disposeDelegate = prev.Dispose;
            }

            if ( disposeDelegate != null )
            {
               var taskToAwait = disposeDelegate( token );
               if ( taskToAwait != null )
               {
                  await taskToAwait;
               }
               retVal = true;
            }
         }

         return retVal;
      }

      protected virtual ValueTask<(Boolean, T, MoveNextAsyncDelegate<T>, ResetAsyncDelegate)> CallInitialMoveNext( CancellationToken token, InitialMoveNextAsyncDelegate<T> initialMoveNext )
      {
         return initialMoveNext( token );
      }

      protected virtual Task AfterMoveNextSucessful( T item )
      {
         return null;
      }

   }

   internal abstract class SequentialEnumeratorCurrentInfo<T>
   {
      public SequentialEnumeratorCurrentInfo(
         MoveNextAsyncDelegate<T> moveNext,
         ResetAsyncDelegate disposeDelegate
      )
      {
         this.MoveNext = moveNext;
         this.Dispose = disposeDelegate;
      }

      public TAsyncToken _token;
      public MoveNextAsyncDelegate<T> MoveNext { get; }
      public ResetAsyncDelegate Dispose { get; }

      public abstract T Current { get; set; }
   }

   internal sealed class SequentialEnumeratorCurrentInfoWithObject<T> : SequentialEnumeratorCurrentInfo<T>
   {
      private Object _current;

      public SequentialEnumeratorCurrentInfoWithObject(
         MoveNextAsyncDelegate<T> moveNext,
         ResetAsyncDelegate disposeDelegate,
         T current
         ) : base( moveNext, disposeDelegate )
      {
         this.Current = current;
      }

      public override T Current
      {
         get => (T) this._current;
         set => Interlocked.Exchange( ref this._current, value );
      }
   }

   internal sealed class SequentialEnumeratorCurrentInfoWithInt32 : SequentialEnumeratorCurrentInfo<Int32>
   {
      private Int32 _current;

      public SequentialEnumeratorCurrentInfoWithInt32(
         MoveNextAsyncDelegate<Int32> moveNext,
         ResetAsyncDelegate disposeDelegate,
         Int32 current
         ) : base( moveNext, disposeDelegate )
      {
         this.Current = current;
      }

      public override Int32 Current
      {
         get => this._current;
         set => Interlocked.Exchange( ref this._current, value );
      }
   }

   internal sealed class SequentialEnumeratorCurrentInfoWithInt64 : SequentialEnumeratorCurrentInfo<Int64>
   {
      private Int64 _current;

      public SequentialEnumeratorCurrentInfoWithInt64(
         MoveNextAsyncDelegate<Int64> moveNext,
         ResetAsyncDelegate disposeDelegate,
         Int64 current
         ) : base( moveNext, disposeDelegate )
      {
         this.Current = current;
      }

      public override Int64 Current
      {
         get => Interlocked.Read( ref this._current );
         set => Interlocked.Exchange( ref this._current, value );
      }
   }



   internal sealed class AsyncSequentialOnlyEnumeratorImplNonObservable<T, TMetadata> : AsyncSequentialOnlyEnumeratorImpl<T>, AsyncEnumerator<T, TMetadata>
   {
      public AsyncSequentialOnlyEnumeratorImplNonObservable(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory,
         TMetadata metadata
         ) : base( initialMoveNext, currentFactory )
      {
         this.Metadata = metadata;
      }

      public TMetadata Metadata { get; }
   }

   internal sealed class AsyncSequentialOnlyEnumeratorImplNonObservable<T> : AsyncSequentialOnlyEnumeratorImpl<T>
   {
      public AsyncSequentialOnlyEnumeratorImplNonObservable(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory
         ) : base( initialMoveNext, currentFactory )
      {
      }
   }

   internal abstract class AsyncSequentialOnlyEnumeratorObservableImpl<T, TStartedArgs, TEndedArgs, TItemArgs> : AsyncSequentialOnlyEnumeratorImpl<T>
      where TStartedArgs : class, EnumerationStartedEventArgs
      where TEndedArgs : class, EnumerationEndedEventArgs
      where TItemArgs : class, EnumerationItemEventArgs<T>
   {
      protected readonly Func<GenericEventHandler<TStartedArgs>> _getGlobalBeforeEnumerationExecutionStart;
      protected readonly Func<GenericEventHandler<TStartedArgs>> _getGlobalAfterEnumerationExecutionStart;
      protected readonly Func<GenericEventHandler<TEndedArgs>> _getGlobalBeforeEnumerationExecutionEnd;
      protected readonly Func<GenericEventHandler<TEndedArgs>> _getGlobalAfterEnumerationExecutionEnd;
      protected readonly Func<GenericEventHandler<TItemArgs>> _getGlobalAfterEnumerationExecutionItemEncountered;

      protected AsyncSequentialOnlyEnumeratorObservableImpl(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory,
         Func<GenericEventHandler<TStartedArgs>> getGlobalBeforeEnumerationExecutionStart,
         Func<GenericEventHandler<TStartedArgs>> getGlobalAfterEnumerationExecutionStart,
         Func<GenericEventHandler<TEndedArgs>> getGlobalBeforeEnumerationExecutionEnd,
         Func<GenericEventHandler<TEndedArgs>> getGlobalAfterEnumerationExecutionEnd,
         Func<GenericEventHandler<TItemArgs>> getGlobalAfterEnumerationExecutionItemEncountered
         ) : base( initialMoveNext, currentFactory )
      {
         this._getGlobalBeforeEnumerationExecutionStart = getGlobalBeforeEnumerationExecutionStart;
         this._getGlobalAfterEnumerationExecutionStart = getGlobalAfterEnumerationExecutionStart;
         this._getGlobalBeforeEnumerationExecutionEnd = getGlobalBeforeEnumerationExecutionEnd;
         this._getGlobalAfterEnumerationExecutionEnd = getGlobalAfterEnumerationExecutionEnd;
         this._getGlobalAfterEnumerationExecutionItemEncountered = getGlobalAfterEnumerationExecutionItemEncountered;
      }

      public event GenericEventHandler<TStartedArgs> BeforeEnumerationStart;
      public event GenericEventHandler<TStartedArgs> AfterEnumerationStart;

      public event GenericEventHandler<TEndedArgs> BeforeEnumerationEnd;
      public event GenericEventHandler<TEndedArgs> AfterEnumerationEnd;

      public event GenericEventHandler<TItemArgs> AfterEnumerationItemEncountered;

      protected override async ValueTask<(Boolean, T, MoveNextAsyncDelegate<T>, ResetAsyncDelegate)> CallInitialMoveNext( CancellationToken token, InitialMoveNextAsyncDelegate<T> initialMoveNext )
      {
         TStartedArgs args = null;
         this.BeforeEnumerationStart?.InvokeAllEventHandlers( evt => evt( ( args = this.CreateBeforeEnumerationStartedArgs() ) ), throwExceptions: false );
         this._getGlobalBeforeEnumerationExecutionStart?.Invoke()?.InvokeAllEventHandlers( evt => evt( args ?? ( args = this.CreateBeforeEnumerationStartedArgs() ) ), throwExceptions: false );
         try
         {
            return await base.CallInitialMoveNext( token, initialMoveNext );
         }
         finally
         {
            this.AfterEnumerationStart?.InvokeAllEventHandlers( evt => evt( ( args = this.CreateAfterEnumerationStartedArgs( args ) ) ), throwExceptions: false );
            this._getGlobalAfterEnumerationExecutionStart?.Invoke()?.InvokeAllEventHandlers( evt => evt( args ?? this.CreateAfterEnumerationStartedArgs( args ) ), throwExceptions: false );
         }
      }

      protected override Task AfterMoveNextSucessful( T item )
      {
         TItemArgs args = null;
         this.AfterEnumerationItemEncountered?.InvokeAllEventHandlers( evt => evt( ( args = this.CreateEnumerationItemArgs( item ) ) ), throwExceptions: false );
         this._getGlobalAfterEnumerationExecutionItemEncountered?.Invoke()?.InvokeAllEventHandlers( evt => evt( args ?? this.CreateEnumerationItemArgs( item ) ), throwExceptions: false );
         return base.AfterMoveNextSucessful( item );
      }

      protected override ValueTask<Boolean> PerformDispose( CancellationToken token, ResetAsyncDelegate disposeDelegate = null )
      {
         TEndedArgs args = null;
         this.BeforeEnumerationEnd?.InvokeAllEventHandlers( evt => evt( ( args = this.CreateBeforeEnumerationEndedArgs() ) ), throwExceptions: false );
         this._getGlobalBeforeEnumerationExecutionEnd?.Invoke()?.InvokeAllEventHandlers( evt => evt( args ?? ( args = this.CreateBeforeEnumerationEndedArgs() ) ), throwExceptions: false );
         try
         {
            return base.PerformDispose( token, disposeDelegate );
         }
         finally
         {
            this.AfterEnumerationEnd?.InvokeAllEventHandlers( evt => evt( ( args = this.CreateAfterEnumerationEndedArgs( args ) ) ), throwExceptions: false );
            this._getGlobalAfterEnumerationExecutionEnd?.Invoke()?.InvokeAllEventHandlers( evt => evt( args ?? this.CreateAfterEnumerationEndedArgs( args ) ), throwExceptions: false );
         }

      }

      protected abstract TStartedArgs CreateBeforeEnumerationStartedArgs();

      protected abstract TStartedArgs CreateAfterEnumerationStartedArgs( TStartedArgs beforeStart );

      protected abstract TItemArgs CreateEnumerationItemArgs( T item );

      protected abstract TEndedArgs CreateBeforeEnumerationEndedArgs();

      protected abstract TEndedArgs CreateAfterEnumerationEndedArgs( TEndedArgs beforeEnd );
   }

   internal sealed class AsyncSequentialOnlyEnumeratorObservableImpl<T> : AsyncSequentialOnlyEnumeratorObservableImpl<T, EnumerationStartedEventArgs, EnumerationEndedEventArgs, EnumerationItemEventArgs<T>>, AsyncEnumeratorObservable<T>
   {
      public AsyncSequentialOnlyEnumeratorObservableImpl(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory,
         Func<GenericEventHandler<EnumerationStartedEventArgs>> getGlobalBeforeEnumerationExecutionStart,
         Func<GenericEventHandler<EnumerationStartedEventArgs>> getGlobalAfterEnumerationExecutionStart,
         Func<GenericEventHandler<EnumerationEndedEventArgs>> getGlobalBeforeEnumerationExecutionEnd,
         Func<GenericEventHandler<EnumerationEndedEventArgs>> getGlobalAfterEnumerationExecutionEnd,
         Func<GenericEventHandler<EnumerationItemEventArgs<T>>> getGlobalAfterEnumerationExecutionItemEncountered
         ) : base( initialMoveNext, currentFactory, getGlobalBeforeEnumerationExecutionStart, getGlobalAfterEnumerationExecutionStart, getGlobalBeforeEnumerationExecutionEnd, getGlobalAfterEnumerationExecutionEnd, getGlobalAfterEnumerationExecutionItemEncountered )
      {
      }

      protected override EnumerationStartedEventArgs CreateBeforeEnumerationStartedArgs()
      {
         return EnumerationEventArgsUtility.StatelessStartArgs;
      }

      protected override EnumerationStartedEventArgs CreateAfterEnumerationStartedArgs( EnumerationStartedEventArgs beforeStart )
      {
         return beforeStart ?? this.CreateBeforeEnumerationStartedArgs();
      }

      protected override EnumerationItemEventArgs<T> CreateEnumerationItemArgs( T item )
      {
         return new EnumerationItemEventArgsImpl<T>( item );
      }

      protected override EnumerationEndedEventArgs CreateBeforeEnumerationEndedArgs()
      {
         return EnumerationEventArgsUtility.StatelessEndArgs;
      }

      protected override EnumerationEndedEventArgs CreateAfterEnumerationEndedArgs( EnumerationEndedEventArgs beforeEnd )
      {
         return beforeEnd ?? this.CreateBeforeEnumerationEndedArgs();
      }
   }

   internal sealed class AsyncSequentialOnlyEnumeratorObservableImpl<T, TMetadata> : AsyncSequentialOnlyEnumeratorObservableImpl<T, EnumerationStartedEventArgs<TMetadata>, EnumerationEndedEventArgs<TMetadata>, EnumerationItemEventArgs<T, TMetadata>>, AsyncEnumeratorObservable<T, TMetadata>
   {

      public AsyncSequentialOnlyEnumeratorObservableImpl(
         InitialMoveNextAsyncDelegate<T> initialMoveNext,
         TSequentialCurrentInfoFactory currentFactory,
         TMetadata metadata,
         Func<GenericEventHandler<EnumerationStartedEventArgs<TMetadata>>> getGlobalBeforeEnumerationExecutionStart,
         Func<GenericEventHandler<EnumerationStartedEventArgs<TMetadata>>> getGlobalAfterEnumerationExecutionStart,
         Func<GenericEventHandler<EnumerationEndedEventArgs<TMetadata>>> getGlobalBeforeEnumerationExecutionEnd,
         Func<GenericEventHandler<EnumerationEndedEventArgs<TMetadata>>> getGlobalAfterEnumerationExecutionEnd,
         Func<GenericEventHandler<EnumerationItemEventArgs<T, TMetadata>>> getGlobalAfterEnumerationExecutionItemEncountered
         ) : base( initialMoveNext, currentFactory, getGlobalBeforeEnumerationExecutionStart, getGlobalAfterEnumerationExecutionStart, getGlobalBeforeEnumerationExecutionEnd, getGlobalAfterEnumerationExecutionEnd, getGlobalAfterEnumerationExecutionItemEncountered )
      {
         this.Metadata = metadata;
      }

      event GenericEventHandler<EnumerationStartedEventArgs> AsyncEnumerationObservation<T>.BeforeEnumerationStart
      {
         add
         {
            this.BeforeEnumerationStart += value;
         }

         remove
         {
            this.BeforeEnumerationStart -= value;
         }
      }

      event GenericEventHandler<EnumerationStartedEventArgs> AsyncEnumerationObservation<T>.AfterEnumerationStart
      {
         add
         {
            this.AfterEnumerationStart += value;
         }

         remove
         {
            this.AfterEnumerationStart -= value;
         }
      }

      event GenericEventHandler<EnumerationItemEventArgs<T>> AsyncEnumerationObservation<T>.AfterEnumerationItemEncountered
      {
         add
         {
            this.AfterEnumerationItemEncountered += value;
         }
         remove
         {
            this.AfterEnumerationItemEncountered -= value;
         }
      }

      event GenericEventHandler<EnumerationEndedEventArgs> AsyncEnumerationObservation<T>.BeforeEnumerationEnd
      {
         add
         {
            this.BeforeEnumerationEnd += value;
         }

         remove
         {
            this.BeforeEnumerationEnd -= value;
         }
      }

      event GenericEventHandler<EnumerationEndedEventArgs> AsyncEnumerationObservation<T>.AfterEnumerationEnd
      {
         add
         {
            this.AfterEnumerationEnd += value;
         }

         remove
         {
            this.AfterEnumerationEnd -= value;
         }
      }

      public TMetadata Metadata { get; }

      protected override EnumerationStartedEventArgs<TMetadata> CreateBeforeEnumerationStartedArgs()
      {
         return new EnumerationStartedEventArgsImpl<TMetadata>( this.Metadata );
      }

      protected override EnumerationStartedEventArgs<TMetadata> CreateAfterEnumerationStartedArgs( EnumerationStartedEventArgs<TMetadata> beforeStart )
      {
         return beforeStart ?? this.CreateBeforeEnumerationStartedArgs();
      }

      protected override EnumerationItemEventArgs<T, TMetadata> CreateEnumerationItemArgs( T item )
      {
         return new EnumerationItemEventArgsImpl<T, TMetadata>( item, this.Metadata );
      }

      protected override EnumerationEndedEventArgs<TMetadata> CreateBeforeEnumerationEndedArgs()
      {
         return new EnumerationEndedEventArgsImpl<TMetadata>( this.Metadata );
      }

      protected override EnumerationEndedEventArgs<TMetadata> CreateAfterEnumerationEndedArgs( EnumerationEndedEventArgs<TMetadata> beforeEnd )
      {
         return beforeEnd ?? this.CreateBeforeEnumerationEndedArgs();
      }
   }


}
