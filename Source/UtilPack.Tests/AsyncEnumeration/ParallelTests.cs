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
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UtilPack.AsyncEnumeration;

namespace UtilPack.Tests.AsyncEnumeration
{
   [TestClass]
   public class ParallelTests
   {
      const Int32 MAX_ITEMS = 10;

      [TestMethod]
      public async Task TestParallelEnumeratorSync()
      {

         var start = MAX_ITEMS;
         var completionState = new Int32[start];
         var r = new Random();
         var enumerator = AsyncEnumeratorFactory.CreateParallelEnumerator(
            () =>
            {
               var decremented = Interlocked.Decrement( ref start );
               return (decremented >= 0, decremented + 1);
            },
            async ( idx, token ) =>
            {
               await Task.Delay( r.Next( 100, 500 ) );
               return completionState.Length - idx;
            },
            null
            );

         var itemsEncountered = await enumerator.EnumerateInParallelAsync( cur =>
         {
            Interlocked.Increment( ref completionState[cur] );
         } );

         Assert.AreEqual( itemsEncountered, completionState.Length );
         Assert.IsTrue( completionState.All( s => s == 1 ) );
      }

      [TestMethod]
      public async Task TestParallelEnumeratorASync()
      {

         var start = MAX_ITEMS;
         var completionState = new Int32[start];
         var r = new Random();
         var enumerator = AsyncEnumeratorFactory.CreateParallelEnumerator(
            () =>
            {
               var decremented = Interlocked.Decrement( ref start );
               return (decremented >= 0, decremented + 1);
            },
            async ( idx, token ) =>
            {
               await Task.Delay( r.Next( 100, 500 ) );
               return completionState.Length - idx;
            },
            null
            );

         var itemsEncountered = await enumerator.EnumerateInParallelAsync( async cur =>
         {
            await Task.Delay( r.Next( 100, 500 ) );
            Interlocked.Increment( ref completionState[cur] );
         } );

         Assert.AreEqual( itemsEncountered, completionState.Length );
         Assert.IsTrue( completionState.All( s => s == 1 ) );
      }

      [TestMethod]
      public void TestParallelEnumeratorCompletelySync()
      {
         var start = MAX_ITEMS * 100000;
         var completionState = new Int32[start];
         var r = new Random();
         var enumerator = AsyncEnumeratorFactory.CreateParallelEnumerator(
            () =>
            {
               var decremented = Interlocked.Decrement( ref start );
               return (decremented >= 0, decremented + 1);
            },
            ( idx, token ) =>
            {
               return new ValueTask<Int32>( completionState.Length - idx );
            },
            null
            );

         var itemsEncounteredTask = enumerator.EnumerateInParallelAsync( cur =>
         {
            Interlocked.Increment( ref completionState[cur] );
         } );

         Assert.IsTrue( itemsEncounteredTask.IsCompleted );
         Assert.AreEqual( itemsEncounteredTask.Result, completionState.Length );
         Assert.IsTrue( completionState.All( s => s == 1 ) );
      }
   }
}
