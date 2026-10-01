#region Namespace
using Axos.BMS.Common.Constants;
using Axos.BMS.Common.DTOs;
using Axos.BMS.Common.DTOs.Transfers;
using Axos.BMS.Common.Enums;
using Axos.BMS.Common.Helper;
using Axos.BMS.Core.Interface;
using Axos.BMS.DataAccess.Data;
using Axos.BMS.DataAccess.Repository.Interfaces;
using Axos.BMS.DataAccess.Repository.Repositories.BankAccounts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Moq;
#endregion

namespace Axos.BMS.Bank.Accounts.Tests.Repository
{
    /// <summary>
    /// Coverage-gap tests for <see cref="TransferRepository"/>. Complements TransferRepositoryTests by covering
    /// every public method that had no test (create/update/delete flows, document linking, approval rules,
    /// payor lookup, tags, history, ACH, number sequences) plus the missing negative/branch paths.
    /// Methods that execute stored procedures (IsDateInClosedPeriodAsync, GetEffectivePostingDateAsync) cannot run on
    /// the EF InMemory provider and are intentionally not covered here.
    /// </summary>
    public sealed class TransferRepositoryCoverageTests : IDisposable
    {
        private readonly ApplicationDbContext _dbContext;
        private readonly Mock<IDataAccessUnitOfWork> _mockUow = new();
        private readonly TransferRepository _repository;

        public TransferRepositoryCoverageTests()
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase(databaseName: $"TransferRepositoryCoverageTests_{Guid.NewGuid()}")
                .Options;

            var configuration = new ConfigurationBuilder().Build();
            var propertyService = new Mock<IDBConnectionPropertyService>().Object;

            _dbContext = new ApplicationDbContext(options, configuration, propertyService);
            _repository = new TransferRepository(_dbContext, _mockUow.Object);

            _mockUow.Setup(u => u.SaveChangesAsync()).Returns(() => _dbContext.SaveChangesAsync());
            _mockUow.Setup(u => u.BeginTransactionAsync()).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.CommitTransactionAsync()).Returns(Task.CompletedTask);
            _mockUow.Setup(u => u.RollbackTransactionAsync()).Returns(Task.CompletedTask);

            var requestRepo = new Mock<IDataAccessRepository<TransferRequest>>();
            requestRepo.Setup(r => r.Query()).Returns(_dbContext.TransferRequests);
            _mockUow.Setup(u => u.Repository<TransferRequest>()).Returns(requestRepo.Object);

            var entityRepo = new Mock<IDataAccessRepository<Entity>>();
            entityRepo.Setup(r => r.Query()).Returns(_dbContext.Entities);
            _mockUow.Setup(u => u.Repository<Entity>()).Returns(entityRepo.Object);

            var bankAccountRepo = new Mock<IDataAccessRepository<BankAccount>>();
            bankAccountRepo.Setup(r => r.Query()).Returns(_dbContext.BankAccounts);
            _mockUow.Setup(u => u.Repository<BankAccount>()).Returns(bankAccountRepo.Object);
        }

        public void Dispose() => _dbContext.Dispose();

        #region Builders

        private static TransferRequest NewRequest(Guid? id = null, Guid? initiatingEntityId = null) => new()
        {
            TransferRequestId = id ?? Guid.NewGuid(),
            InitiatingEntityId = initiatingEntityId ?? Guid.NewGuid(),
            TransferRefId = "PRF-101",
            TransferStatusCode = "draft",
            TransferTypeCode = "ACCOUNT",
            IsActive = true
        };

        private static Transfer NewTransfer(Guid requestId, Guid entityId, string legCode, bool isActive = true) => new()
        {
            TransferId = Guid.NewGuid(),
            TransferRequestId = requestId,
            EntityId = entityId,
            TransferLegCode = legCode,
            IsActive = isActive,
            CurrencyCode = "USD",
            ProcessingStatusCode = "DRAFT",
            TransferRefId = "PRF-101",
            TransferScopeCode = "WITHIN",
            TransferTypeCode = "ACCOUNT"
        };

        private static TransferChangeLog NewChangeLog(Guid transactionId, DateTime? changeDate = null, DateTime? createdDate = null) => new()
        {
            TransferChangeLogId = Guid.NewGuid(),
            TransactionId = transactionId,
            ChangeDatetime = changeDate ?? DateTime.UtcNow,
            CreatedDate = createdDate ?? DateTime.UtcNow
        };

        private static Document NewDocument(Guid? id = null, bool isActive = true, bool isDeleted = false) => new()
        {
            DocumentId = id ?? Guid.NewGuid(),
            IsActive = isActive,
            IsDeleted = isDeleted,
            IsRecycleBin = false,
            IsModuleLinked = false,
            DocLink = ""
        };

        #endregion

        #region GetTransferRefNumberAsync

        [Fact]
        public async Task GetTransferRefNumberAsync_NoRequests_ReturnsSequenceOne()
        {
            var entityId = Guid.Parse("abcdef12-0000-0000-0000-000000000000");

            var result = await _repository.GetTransferRefNumberAsync(Guid.NewGuid(), entityId);

            Assert.Equal("TRF-ABCDEF-00001", result);
        }

        [Fact]
        public async Task GetTransferRefNumberAsync_WithExistingRequests_IncrementsCount()
        {
            var entityId = Guid.Parse("abcdef12-0000-0000-0000-000000000000");
            _dbContext.TransferRequests.AddRange(NewRequest(), NewRequest());
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetTransferRefNumberAsync(Guid.NewGuid(), entityId);

            Assert.Equal("TRF-ABCDEF-00003", result);
        }

        #endregion

        #region GetEntityPayorAddressAsync

        [Fact]
        public async Task GetEntityPayorAddressAsync_EntityNotFound_ReturnsNull()
        {
            var result = await _repository.GetEntityPayorAddressAsync(Guid.NewGuid(), "HOME");

            Assert.Null(result);
        }

        [Fact]
        public async Task GetEntityPayorAddressAsync_EntityWithoutContact_ReturnsDtoWithoutAddress()
        {
            var entityId = Guid.NewGuid();
            _dbContext.Entities.Add(new Entity { Id = entityId, EntityName = "No Contact Co", IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetEntityPayorAddressAsync(entityId, "HOME");

            Assert.NotNull(result);
            Assert.Equal(entityId, result!.EntityId);
            Assert.Equal("No Contact Co", result.EntityName);
            Assert.Equal(string.Empty, result.Phone);
        }

        [Fact]
        public async Task GetEntityPayorAddressAsync_FullContactGraph_ReturnsPhoneAndTrimmedStreets()
        {
            var entityId = Guid.NewGuid();
            var contactId = Guid.NewGuid();
            var contactInfoId = Guid.NewGuid();
            var contactTypeId = Guid.NewGuid();
            var addressTypeId = Guid.NewGuid();
            var countryId = Guid.NewGuid();

            _dbContext.Entities.Add(new Entity { Id = entityId, EntityName = "Payor Co", BusinessPartyId = contactId, IsActive = true });
            _dbContext.Contacts.Add(new Contact { ContactId = contactId, ContactInfoId = contactInfoId });
            _dbContext.ContactInformations.Add(new ContactInformation { ContactInfoId = contactInfoId, ContactTypeId = contactTypeId, Value = "5551234567" });
            _dbContext.ContactTypes.Add(new ContactType { ContactTypeId = contactTypeId, TypeCode = Constants.ContactTypeCodePhone });
            _dbContext.AddressDetails.Add(new AddressDetail
            {
                AddressDetailId = contactInfoId,
                AddressTypeId = addressTypeId,
                CountryId = countryId,
                AddressLineOne = "  123 Main St  ",
                AddressLineTwo = "   ",
                AddressLineThree = null,
                AddressLineFour = "Suite 9",
                City = "Irvine",
                State = "CA",
                PostalCode = "92618"
            });
            _dbContext.AddressTypes.Add(new AddressType { AddressTypeId = addressTypeId, TypeCode = "HOME" });
            _dbContext.Countries.Add(new Country { CountryId = countryId, Iso = "US", Iso3 = "USA" });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetEntityPayorAddressAsync(entityId, "HOME");

            Assert.NotNull(result);
            Assert.Equal("5551234567", result!.Phone);
            Assert.Equal(new List<string> { "123 Main St", "Suite 9" }, result.Streets);
            Assert.Equal("Irvine", result.City);
            Assert.Equal("CA", result.State);
            Assert.Equal("92618", result.ZipCode);
            Assert.Equal("US", result.CountryCode);
        }

        [Fact]
        public async Task GetEntityPayorAddressAsync_AddressTypeMismatch_ReturnsDtoWithoutAddress()
        {
            var entityId = Guid.NewGuid();
            var contactId = Guid.NewGuid();
            var contactInfoId = Guid.NewGuid();
            var addressTypeId = Guid.NewGuid();

            _dbContext.Entities.Add(new Entity { Id = entityId, EntityName = "Payor Co", BusinessPartyId = contactId, IsActive = true });
            _dbContext.Contacts.Add(new Contact { ContactId = contactId, ContactInfoId = contactInfoId });
            _dbContext.AddressDetails.Add(new AddressDetail { AddressDetailId = contactInfoId, AddressTypeId = addressTypeId, AddressLineOne = "1 Way", City = "X" });
            _dbContext.AddressTypes.Add(new AddressType { AddressTypeId = addressTypeId, TypeCode = "OFFICE" });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetEntityPayorAddressAsync(entityId, "HOME");

            Assert.NotNull(result);
            Assert.Empty(result!.Streets);
            Assert.True(string.IsNullOrEmpty(result.City));
        }

        #endregion

        #region GetExistingPayorIdByEntityAndNameAsync

        [Fact]
        public async Task GetExistingPayorIdByEntityAndNameAsync_EmptyEntityId_ReturnsNull()
        {
            var result = await _repository.GetExistingPayorIdByEntityAndNameAsync(Guid.Empty, "Acme");

            Assert.Null(result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetExistingPayorIdByEntityAndNameAsync_BlankName_ReturnsNull(string? name)
        {
            var result = await _repository.GetExistingPayorIdByEntityAndNameAsync(Guid.NewGuid(), name!);

            Assert.Null(result);
        }

        [Fact]
        public async Task GetExistingPayorIdByEntityAndNameAsync_MatchIgnoresCaseAndWhitespace_ReturnsPayorId()
        {
            var entityId = Guid.NewGuid();
            var payorId = Guid.NewGuid();
            _dbContext.Payors.AddRange(
                new Payor { PayorId = payorId, EntityId = entityId, PayorName = "  Acme & Co.  ", IsActive = true },
                new Payor { PayorId = Guid.NewGuid(), EntityId = entityId, PayorName = "Acme & Co.", IsActive = false },
                new Payor { PayorId = Guid.NewGuid(), EntityId = Guid.NewGuid(), PayorName = "Acme & Co.", IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetExistingPayorIdByEntityAndNameAsync(entityId, "acme & CO.");

            Assert.Equal(payorId, result);
        }

        [Fact]
        public async Task GetExistingPayorIdByEntityAndNameAsync_NoMatch_ReturnsNull()
        {
            var entityId = Guid.NewGuid();
            _dbContext.Payors.Add(new Payor { PayorId = Guid.NewGuid(), EntityId = entityId, PayorName = "Other", IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetExistingPayorIdByEntityAndNameAsync(entityId, "Acme");

            Assert.Null(result);
        }

        #endregion

        #region GetNextTransferNumberAsync / GetNextDraftNumberAsync

        [Fact]
        public async Task GetNextTransferNumberAsync_NoSequence_CreatesSequenceStartingAtOne()
        {
            var entityId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var userId = Guid.NewGuid();

            var result = await _repository.GetNextTransferNumberAsync(entityId, customerId, userId);

            Assert.Equal(1, result);
            var seq = await _dbContext.TransferNumberSequences.SingleAsync(s => s.EntityId == entityId);
            Assert.Equal(1, seq.LastTransferNumber);
            Assert.Equal(userId, seq.CreatedBy);
        }

        [Fact]
        public async Task GetNextTransferNumberAsync_ExistingSequence_IncrementsAndStampsModifiedBy()
        {
            var entityId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            _dbContext.TransferNumberSequences.Add(new TransferNumberSequence
            {
                EntityId = entityId,
                CustomerId = customerId,
                LastTransferNumber = 7,
                CreatedBy = Guid.NewGuid(),
                CreatedDate = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetNextTransferNumberAsync(entityId, customerId, userId);

            Assert.Equal(8, result);
            var seq = await _dbContext.TransferNumberSequences.SingleAsync(s => s.EntityId == entityId);
            Assert.Equal(8, seq.LastTransferNumber);
            Assert.Equal(userId, seq.ModifiedBy);
            Assert.NotNull(seq.ModifiedDate);
        }

        [Fact]
        public async Task GetNextTransferNumberAsync_ExistingRequestAheadOfSequence_UsesRequestNumber()
        {
            var entityId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var request = NewRequest();
            request.TransferNumber = 10;
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.Add(NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement));
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetNextTransferNumberAsync(entityId, customerId, Guid.NewGuid());

            Assert.Equal(11, result);
        }

        [Fact]
        public async Task GetNextDraftNumberAsync_ExistingRequestAheadOfSequence_UsesRequestNumber()
        {
            var entityId = Guid.NewGuid();
            var customerId = Guid.NewGuid();
            var userId = Guid.NewGuid();
            var request = NewRequest();
            request.DraftNumber = 20;
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.Add(NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement));
            _dbContext.TransferNumberSequences.Add(new TransferNumberSequence
            {
                EntityId = entityId,
                CustomerId = customerId,
                LastDraftNumber = 3,
                CreatedBy = userId,
                CreatedDate = DateTime.UtcNow
            });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetNextDraftNumberAsync(entityId, customerId, userId);

            Assert.Equal(21, result);
            var seq = await _dbContext.TransferNumberSequences.SingleAsync(s => s.EntityId == entityId);
            Assert.Equal(21, seq.LastDraftNumber);
            Assert.Equal(userId, seq.ModifiedBy);
        }

        #endregion

        #region GetTransferRequestByIdAsync / GetTransferRequestAsync

        [Fact]
        public async Task GetTransferRequestByIdAsync_Active_ReturnsRequest()
        {
            var request = NewRequest();
            _dbContext.TransferRequests.Add(request);
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetTransferRequestByIdAsync(request.TransferRequestId);

            Assert.NotNull(result);
            Assert.Equal(request.TransferRequestId, result!.TransferRequestId);
        }

        [Fact]
        public async Task GetTransferRequestByIdAsync_InactiveOrMissing_ReturnsNull()
        {
            var inactive = NewRequest();
            inactive.IsActive = false;
            _dbContext.TransferRequests.Add(inactive);
            await _dbContext.SaveChangesAsync();

            Assert.Null(await _repository.GetTransferRequestByIdAsync(inactive.TransferRequestId));
            Assert.Null(await _repository.GetTransferRequestByIdAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task GetTransferRequestAsync_BothLegs_ReturnsRequestAndLegs()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var from = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement);
            var to = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Deposit);
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.AddRange(from, to);
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetTransferRequestAsync(request.TransferRequestId);

            Assert.NotNull(result);
            Assert.Equal(from.TransferId, result!.Value.From.TransferId);
            Assert.Equal(to.TransferId, result.Value.To.TransferId);
        }

        [Fact]
        public async Task GetTransferRequestAsync_RequestMissing_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransferRequestAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task GetTransferRequestAsync_MissingDepositLeg_ReturnsNull()
        {
            var request = NewRequest();
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.Add(NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement));
            await _dbContext.SaveChangesAsync();

            Assert.Null(await _repository.GetTransferRequestAsync(request.TransferRequestId));
        }

        #endregion

        #region CreateTransferAsync / CreateDraftAsync

        [Fact]
        public async Task CreateTransferAsync_ValidInput_PersistsAllAndCommits()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var transfers = new List<Transfer>
            {
                NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement),
                NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Deposit)
            };
            var log = NewChangeLog(request.TransferRequestId);

            await _repository.CreateTransferAsync(transfers, request, log);

            Assert.Equal(1, await _dbContext.TransferRequests.CountAsync());
            Assert.Equal(2, await _dbContext.Transfers.CountAsync());
            Assert.Equal(1, await _dbContext.TransferChangeLogs.CountAsync());
            _mockUow.Verify(u => u.BeginTransactionAsync(), Times.Once);
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Once);
            _mockUow.Verify(u => u.RollbackTransactionAsync(), Times.Never);
        }

        [Fact]
        public async Task CreateTransferAsync_NoTransfersAndEmptyLogId_OnlyPersistsRequest()
        {
            var request = NewRequest();
            var log = new TransferChangeLog { TransferChangeLogId = Guid.Empty };

            await _repository.CreateTransferAsync(new List<Transfer>(), request, log);

            Assert.Equal(1, await _dbContext.TransferRequests.CountAsync());
            Assert.Equal(0, await _dbContext.Transfers.CountAsync());
            Assert.Equal(0, await _dbContext.TransferChangeLogs.CountAsync());
        }

        [Fact]
        public async Task CreateTransferAsync_NullChangeLog_DoesNotThrow()
        {
            var request = NewRequest();

            await _repository.CreateTransferAsync(new List<Transfer>(), request, null!);

            Assert.Equal(1, await _dbContext.TransferRequests.CountAsync());
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Once);
        }

        [Fact]
        public async Task CreateTransferAsync_SaveFails_RollsBackAndRethrows()
        {
            _mockUow.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.CreateTransferAsync(new List<Transfer>(), NewRequest(), NewChangeLog(Guid.NewGuid())));

            _mockUow.Verify(u => u.RollbackTransactionAsync(), Times.Once);
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Never);
        }

        [Fact]
        public async Task CreateDraftAsync_ValidInput_PersistsRequestAndLogAndCommits()
        {
            var request = NewRequest();
            var log = NewChangeLog(request.TransferRequestId);

            await _repository.CreateDraftAsync(request, log);

            Assert.Equal(1, await _dbContext.TransferRequests.CountAsync());
            Assert.Equal(1, await _dbContext.TransferChangeLogs.CountAsync());
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Once);
        }

        [Fact]
        public async Task CreateDraftAsync_EmptyOrNullLog_SkipsLog()
        {
            await _repository.CreateDraftAsync(NewRequest(), new TransferChangeLog { TransferChangeLogId = Guid.Empty });
            await _repository.CreateDraftAsync(NewRequest(), null!);

            Assert.Equal(2, await _dbContext.TransferRequests.CountAsync());
            Assert.Equal(0, await _dbContext.TransferChangeLogs.CountAsync());
        }

        [Fact]
        public async Task CreateDraftAsync_SaveFails_RollsBackAndRethrows()
        {
            _mockUow.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.CreateDraftAsync(NewRequest(), NewChangeLog(Guid.NewGuid())));

            _mockUow.Verify(u => u.RollbackTransactionAsync(), Times.Once);
        }

        #endregion

        #region Update methods / SaveChangesAsync

        [Fact]
        public async Task UpdateTransferRequestAsync_PersistsChanges()
        {
            var request = NewRequest();
            _dbContext.TransferRequests.Add(request);
            await _dbContext.SaveChangesAsync();

            request.TransferStatusCode = "PENDING";
            await _repository.UpdateTransferRequestAsync(request);

            var saved = await _dbContext.TransferRequests.AsNoTracking().SingleAsync(r => r.TransferRequestId == request.TransferRequestId);
            Assert.Equal("PENDING", saved.TransferStatusCode);
        }

        [Fact]
        public async Task UpdateTransferAsync_PersistsTransferAndRequest()
        {
            var request = NewRequest();
            var transfer = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement);
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.Add(transfer);
            await _dbContext.SaveChangesAsync();

            transfer.ProcessingStatusCode = "SUBMITTED";
            request.TransferStatusCode = "SUBMITTED";
            await _repository.UpdateTransferAsync(transfer, request);

            Assert.Equal("SUBMITTED", (await _dbContext.Transfers.AsNoTracking().SingleAsync()).ProcessingStatusCode);
            Assert.Equal("SUBMITTED", (await _dbContext.TransferRequests.AsNoTracking().SingleAsync()).TransferStatusCode);
        }

        [Fact]
        public async Task UpdateWorkflowAsync_PersistsWorkflowChanges()
        {
            var id = Guid.NewGuid();
            var instance = new WorkflowInstance { WorkflowSourceEntityId = id, Status = "Open" };
            var source = new WorkflowSourceEntity { WorkflowSourceEntityId = id, SourceData = "x", Status = "Open" };
            _dbContext.WorkflowInstances.Add(instance);
            _dbContext.WorkflowSourceEntities.Add(source);
            await _dbContext.SaveChangesAsync();

            instance.Status = "Closed";
            source.Status = "Closed";
            await _repository.UpdateWorkflowAsync(instance, source);

            Assert.Equal("Closed", (await _dbContext.WorkflowInstances.AsNoTracking().SingleAsync()).Status);
            Assert.Equal("Closed", (await _dbContext.WorkflowSourceEntities.AsNoTracking().SingleAsync()).Status);
        }

        [Fact]
        public async Task SaveChangesAsync_DelegatesToUnitOfWork()
        {
            await _repository.SaveChangesAsync();

            _mockUow.Verify(u => u.SaveChangesAsync(), Times.Once);
        }

        [Fact]
        public async Task UpdateGlJournalHeaderDescriptionAsync_HeaderExists_UpdatesDescription()
        {
            var headerId = Guid.NewGuid();
            _dbContext.GljournalEntryHeaders.Add(new GljournalEntryHeader { GljournalEntryHeaderId = headerId });
            await _dbContext.SaveChangesAsync();

            await _repository.UpdateGlJournalHeaderDescriptionAsync(headerId, "new description");

            var saved = await _dbContext.GljournalEntryHeaders.AsNoTracking().SingleAsync();
            Assert.Equal("new description", saved.Description);
        }

        [Fact]
        public async Task UpdateGlJournalHeaderDescriptionAsync_HeaderMissing_DoesNothing()
        {
            await _repository.UpdateGlJournalHeaderDescriptionAsync(Guid.NewGuid(), "ignored");

            Assert.Equal(0, await _dbContext.GljournalEntryHeaders.CountAsync());
        }

        #endregion

        #region Delete flows

        [Fact]
        public async Task HardDeleteTransferAsync_WithReason_StampsDeletionAuditAndRemovesRows()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var from = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement);
            var to = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Deposit);
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.AddRange(from, to);
            _dbContext.TransferChangeLogs.Add(NewChangeLog(request.TransferRequestId));
            await _dbContext.SaveChangesAsync();
            var userId = Guid.NewGuid();

            await _repository.HardDeleteTransferAsync(request, from, to, "duplicate", userId);

            Assert.Equal(0, await _dbContext.TransferRequests.CountAsync());
            Assert.Equal(0, await _dbContext.Transfers.CountAsync());
            Assert.Equal(0, await _dbContext.TransferChangeLogs.CountAsync());
            Assert.Equal("duplicate", request.DeletionReason);
            Assert.Equal(userId, request.DeletedBy);
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Once);
        }

        [Fact]
        public async Task HardDeleteTransferAsync_SaveFails_RollsBackAndRethrows()
        {
            var request = NewRequest();
            var from = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement);
            var to = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Deposit);
            _mockUow.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.HardDeleteTransferAsync(request, from, to, "reason", Guid.NewGuid()));

            _mockUow.Verify(u => u.RollbackTransactionAsync(), Times.Once);
            _mockUow.Verify(u => u.CommitTransactionAsync(), Times.Never);
        }

        [Fact]
        public async Task SoftDeleteTransferAsync_SaveFails_RollsBackAndRethrows()
        {
            var request = NewRequest();
            var from = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement);
            var to = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Deposit);
            _mockUow.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("boom"));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.SoftDeleteTransferAsync(request, from, to));

            _mockUow.Verify(u => u.RollbackTransactionAsync(), Times.Once);
        }

        #endregion

        #region Change log

        [Fact]
        public async Task CreateTransfersChangeLogAsync_AddsRow()
        {
            var requestId = Guid.NewGuid();

            await _repository.CreateTransfersChangeLogAsync(NewChangeLog(requestId));

            Assert.Equal(1, await _dbContext.TransferChangeLogs.CountAsync(l => l.TransactionId == requestId));
        }

        [Fact]
        public async Task WriteChangeLogsAsync_NullOrEmpty_DoesNotSave()
        {
            await _repository.WriteChangeLogsAsync(null!);
            await _repository.WriteChangeLogsAsync(new List<TransferChangeLog>());

            _mockUow.Verify(u => u.SaveChangesAsync(), Times.Never);
        }

        [Fact]
        public async Task GetHistoryAsync_ReturnsOnlyRequestedTransactionOrderedByChangeThenCreatedDate()
        {
            var requestId = Guid.NewGuid();
            var t0 = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var third = NewChangeLog(requestId, t0.AddDays(2), t0);
            var secondB = NewChangeLog(requestId, t0.AddDays(1), t0.AddHours(5));
            var secondA = NewChangeLog(requestId, t0.AddDays(1), t0.AddHours(1));
            var other = NewChangeLog(Guid.NewGuid(), t0, t0);
            _dbContext.TransferChangeLogs.AddRange(third, secondB, secondA, other);
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetHistoryAsync(requestId);

            Assert.Equal(
                new[] { secondA.TransferChangeLogId, secondB.TransferChangeLogId, third.TransferChangeLogId },
                result.Select(r => r.TransferChangeLogId).ToArray());
        }

        #endregion

        #region Lookup helpers

        [Fact]
        public async Task GetBankAccountTransferInfoAsync_Found_ReturnsAccountNumberWithoutType()
        {
            var bankAccountId = Guid.NewGuid();
            var entityId = Guid.NewGuid();
            _dbContext.BankAccounts.Add(new BankAccount { BankAccountId = bankAccountId, EntityId = entityId, BankAccountNumber = "98765" });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetBankAccountTransferInfoAsync(bankAccountId, entityId);

            Assert.NotNull(result);
            Assert.Equal("98765", result!.BankAccountNumber);
            Assert.Null(result.AccountTypeCode);
        }

        [Fact]
        public async Task GetBankAccountTransferInfoAsync_NotFound_ReturnsNull()
        {
            Assert.Null(await _repository.GetBankAccountTransferInfoAsync(Guid.NewGuid(), Guid.NewGuid()));
        }

        [Fact]
        public async Task GetEntityDetailsByIds_ReturnsOnlyRequested()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            _dbContext.Entities.AddRange(
                new Entity { Id = a, EntityName = "A", IsActive = true },
                new Entity { Id = b, EntityName = "B", IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetEntityDetailsByIds(new[] { a });

            Assert.Single(result);
            Assert.Equal(a, result[0].Id);
        }

        [Fact]
        public async Task GetBankAccountDetailsByIds_ReturnsOnlyRequested()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            _dbContext.BankAccounts.AddRange(
                new BankAccount { BankAccountId = a, EntityId = Guid.NewGuid() },
                new BankAccount { BankAccountId = b, EntityId = Guid.NewGuid() });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetBankAccountDetailsByIds(new[] { b });

            Assert.Single(result);
            Assert.Equal(b, result[0].BankAccountId);
        }

        [Fact]
        public async Task GetChartAccountDetailsByIdsAsync_ReturnsOnlyRequested()
        {
            var a = Guid.NewGuid();
            var b = Guid.NewGuid();
            _dbContext.ChartOfAccounts.AddRange(
                new ChartOfAccount { ChartOfAccountsId = a, IsActive = true },
                new ChartOfAccount { ChartOfAccountsId = b, IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetChartAccountDetailsByIdsAsync(new List<Guid> { a });

            Assert.Single(result);
            Assert.Equal(a, result[0].ChartOfAccountsId);
        }

        [Fact]
        public async Task IsEntityActiveAsync_ActiveInactiveAndMissing()
        {
            var active = Guid.NewGuid();
            var inactive = Guid.NewGuid();
            _dbContext.Entities.AddRange(
                new Entity { Id = active, EntityName = "A", IsActive = true },
                new Entity { Id = inactive, EntityName = "I", IsActive = false });
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.IsEntityActiveAsync(active));
            Assert.False(await _repository.IsEntityActiveAsync(inactive));
            Assert.False(await _repository.IsEntityActiveAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task IsBankAccountActiveAsync_ExistingAndMissing()
        {
            var id = Guid.NewGuid();
            _dbContext.BankAccounts.Add(new BankAccount { BankAccountId = id, EntityId = Guid.NewGuid() });
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.IsBankAccountActiveAsync(id));
            Assert.False(await _repository.IsBankAccountActiveAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task IsBankAccountClosedAtBankAsync_NotClosed_ReturnsFalse()
        {
            var id = Guid.NewGuid();
            _dbContext.BankAccounts.Add(new BankAccount { BankAccountId = id, EntityId = Guid.NewGuid(), BankAccountStatusType = BankAccountStatusTypeEnum.ACTIVE });
            await _dbContext.SaveChangesAsync();

            Assert.False(await _repository.IsBankAccountClosedAtBankAsync(id));
        }

        [Fact]
        public async Task GetDisbursementAndDepositLegByRequestId_EmptyGuid_ReturnsNull()
        {
            Assert.Null(await _repository.GetDisbursementTransferLegByRequestId(Guid.Empty));
            Assert.Null(await _repository.GetDepsoitTransferLegByRequestId(Guid.Empty));
        }

        [Fact]
        public async Task GetTransferWithRequestByTransferIdAsync_EmptyGuid_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransferWithRequestByTransferIdAsync(Guid.Empty));
        }

        [Fact]
        public async Task GetTransferWithRequestByTransferRequestIdAsync_RequestWithoutTransfer_ReturnsNull()
        {
            var request = NewRequest();
            _dbContext.TransferRequests.Add(request);
            await _dbContext.SaveChangesAsync();

            Assert.Null(await _repository.GetTransferWithRequestByTransferRequestIdAsync(request.TransferRequestId));
        }

        [Fact]
        public async Task GetTransferWorkflowAsync_SourceEntityMissing_ReturnsNull()
        {
            var id = Guid.NewGuid();
            _dbContext.WorkflowInstances.Add(new WorkflowInstance { WorkflowSourceEntityId = id, Status = "Open" });
            await _dbContext.SaveChangesAsync();

            Assert.Null(await _repository.GetTransferWorkflowAsync(id));
        }

        [Fact]
        public async Task GetTransfersWithRequestAsync_TransfersWithoutRequest_ReturnsNull()
        {
            var requestId = Guid.NewGuid();
            _dbContext.Transfers.Add(NewTransfer(requestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement));
            await _dbContext.SaveChangesAsync();

            Assert.Null(await _repository.GetTransfersWithRequestAsync(requestId));
        }

        [Fact]
        public async Task GetMappingGlAccountsAsync_NoMapping_ReturnsNulls()
        {
            var result = await _repository.GetMappingGlAccountsAsync(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

            Assert.Null(result.EntityAGl);
            Assert.Null(result.EntityBGl);
        }

        [Fact]
        public async Task GetMappingGlAccountsAsync_ReversedMapping_OrientsToEntityA()
        {
            var enterpriseId = Guid.NewGuid();
            var entityA = Guid.NewGuid();
            var entityB = Guid.NewGuid();
            var glA = Guid.NewGuid();
            var glB = Guid.NewGuid();
            _dbContext.InterEntityTransferGlAccountMappings.Add(new InterEntityTransferGlAccountMapping
            {
                EnterpriseId = enterpriseId,
                PrimaryEntityId = entityB,
                OtherEntityId = entityA,
                PrimaryEntityGLAccountId = glB,
                OtherEntityGLAccountId = glA,
                IsActive = true
            });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetMappingGlAccountsAsync(entityA, entityB, enterpriseId);

            Assert.Equal(glA, result.EntityAGl);
            Assert.Equal(glB, result.EntityBGl);
            Assert.True(await _repository.HasActiveGlMappingAsync(entityA, entityB, enterpriseId));
        }

        [Fact]
        public async Task GetTransferOriginInfoAsync_Found_ReturnsTuple()
        {
            var request = NewRequest();
            request.TransferNumber = 42;
            var transfer = NewTransfer(request.TransferRequestId, Guid.NewGuid(), TransferCodes.TransferLegCode.Disbursement);
            _dbContext.TransferRequests.Add(request);
            _dbContext.Transfers.Add(transfer);
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetTransferOriginInfoAsync(transfer.TransferId);

            Assert.NotNull(result);
            Assert.Equal("ACCOUNT", result!.Value.TransferTypeCode);
            Assert.Equal("PRF-101", result.Value.TransferRefId);
            Assert.Equal(request.TransferRequestId, result.Value.TransferRequestId);
            Assert.Equal(42, result.Value.TransferNumber);
        }

        [Fact]
        public async Task GetTransferOriginInfoAsync_NotFound_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransferOriginInfoAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task GetDepositIdBySourceReferenceIdAsync_ReturnsMostRecentMatch()
        {
            var transferId = Guid.NewGuid();
            var older = Guid.NewGuid();
            var newer = Guid.NewGuid();
            _dbContext.DepositHeaders.AddRange(
                new DepositHeader { DepositId = older, DepositSourceType = Constants.BANK_RECON, DepositSourceReferenceId = transferId.ToString(), CreatedDate = DateTime.UtcNow.AddDays(-2) },
                new DepositHeader { DepositId = newer, DepositSourceType = Constants.BANK_RECON, DepositSourceReferenceId = transferId.ToString(), CreatedDate = DateTime.UtcNow },
                new DepositHeader { DepositId = Guid.NewGuid(), DepositSourceType = "OTHER", DepositSourceReferenceId = transferId.ToString(), CreatedDate = DateTime.UtcNow });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetDepositIdBySourceReferenceIdAsync(transferId);

            Assert.Equal(newer, result);
        }

        [Fact]
        public async Task GetDepositIdBySourceReferenceIdAsync_NoMatch_ReturnsNull()
        {
            Assert.Null(await _repository.GetDepositIdBySourceReferenceIdAsync(Guid.NewGuid()));
        }

        #endregion

        #region Transfer ACH

        private static TransferACH NewAch(Guid transferId, DateTime createdDate) => new()
        {
            TransferACHId = Guid.NewGuid(),
            TransferId = transferId,
            EntityId = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            EnterpriseId = Guid.NewGuid(),
            TransferRefId = "TRF-12345",
            ACHTransactionType = "CREDIT",
            OriginatingAccountNumber = "123456789",
            OriginatingRoutingNumber = "987654321",
            SECCode = "9900",
            ACHStatusCode = "PENDING",
            CreatedDate = createdDate,
            CreatedBy = Guid.NewGuid(),
            IsActive = true
        };

        [Fact]
        public async Task GetTransferAchByTransferAsync_ReturnsLatestRecord()
        {
            var transferId = Guid.NewGuid();
            var older = NewAch(transferId, DateTime.UtcNow.AddDays(-1));
            var newer = NewAch(transferId, DateTime.UtcNow);
            _dbContext.TransferAChes.AddRange(older, newer, NewAch(Guid.NewGuid(), DateTime.UtcNow));
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetTransferAchByTransferAsync(transferId);

            Assert.NotNull(result);
            Assert.Equal(newer.TransferACHId, result!.TransferACHId);
        }

        [Fact]
        public async Task GetTransferAchByTransferAsync_NotFound_ReturnsNull()
        {
            Assert.Null(await _repository.GetTransferAchByTransferAsync(Guid.NewGuid()));
        }

        [Fact]
        public async Task UpdateTransferAchAsync_PersistsStatusChange()
        {
            var ach = NewAch(Guid.NewGuid(), DateTime.UtcNow);
            _dbContext.TransferAChes.Add(ach);
            await _dbContext.SaveChangesAsync();

            ach.ACHStatusCode = "SENT";
            await _repository.UpdateTransferAchAsync(ach);

            Assert.Equal("SENT", (await _dbContext.TransferAChes.AsNoTracking().SingleAsync()).ACHStatusCode);
        }

        [Fact]
        public async Task CreateTransferAchAsync_PersistsRecord()
        {
            var ach = NewAch(Guid.NewGuid(), DateTime.UtcNow);

            await _repository.CreateTransferAchAsync(ach);

            Assert.Equal(1, await _dbContext.TransferAChes.CountAsync(a => a.TransferACHId == ach.TransferACHId));
        }

        #endregion

        #region HasActiveApprovalRuleAsync

        private async Task<(Guid customerId, Guid entityId, Guid enterpriseId, Guid bankAccountId, Guid functionId)> SeedModuleFunctionAsync(string name)
        {
            var functionId = Guid.NewGuid();
            _dbContext.ModuleFunctions.Add(new ModuleFunction { ModuleFunctionId = functionId, ModuleFunctionName = name });
            await _dbContext.SaveChangesAsync();
            return (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), functionId);
        }

        private static ApprovalRule NewRule(Guid customerId, Guid functionId, bool isActive = true, bool? approvalRequired = true) => new()
        {
            ApprovalRuleId = Guid.NewGuid(),
            CustomerId = customerId,
            ModulefunctionId = functionId,
            IsActive = isActive,
            IsApprovalRequired = approvalRequired
        };

        [Fact]
        public async Task HasActiveApprovalRuleAsync_MatchingModuleObjectRule_ReturnsTrue()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            var rule = NewRule(c, f);
            rule.moduleObjectId = b;
            _dbContext.ApprovalRules.Add(rule);
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_MatchingEntityRule_ReturnsTrue()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            var rule = NewRule(c, f, approvalRequired: null);
            rule.EntityId = e;
            _dbContext.ApprovalRules.Add(rule);
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_EnterpriseLevelRule_ReturnsTrue()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            var rule = NewRule(c, f);
            rule.EnterpriseId = ent;
            _dbContext.ApprovalRules.Add(rule);
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_CustomerLevelRule_ReturnsTrue()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            _dbContext.ApprovalRules.Add(NewRule(c, f));
            await _dbContext.SaveChangesAsync();

            Assert.True(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_ApprovalNotRequired_ReturnsFalse()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            _dbContext.ApprovalRules.Add(NewRule(c, f, approvalRequired: false));
            await _dbContext.SaveChangesAsync();

            Assert.False(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_InactiveRule_ReturnsFalse()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Transfers");
            _dbContext.ApprovalRules.Add(NewRule(c, f, isActive: false));
            await _dbContext.SaveChangesAsync();

            Assert.False(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        [Fact]
        public async Task HasActiveApprovalRuleAsync_DifferentFunctionName_ReturnsFalse()
        {
            var (c, e, ent, b, f) = await SeedModuleFunctionAsync("Payors");
            _dbContext.ApprovalRules.Add(NewRule(c, f));
            await _dbContext.SaveChangesAsync();

            Assert.False(await _repository.HasActiveApprovalRuleAsync(c, e, ent, b, "Transfers"));
        }

        #endregion

        #region Documents

        [Fact]
        public async Task GetDocumentIdsAsync_ReturnsOnlyActiveLinkedNonRecycledDocuments()
        {
            var requestId = Guid.NewGuid();
            var good = NewDocument(); good.IsModuleLinked = true;
            var notLinked = NewDocument(); notLinked.IsModuleLinked = false;
            var recycled = NewDocument(); recycled.IsModuleLinked = true; recycled.IsRecycleBin = true;
            var inactiveDoc = NewDocument(isActive: false); inactiveDoc.IsModuleLinked = true;
            _dbContext.Documents.AddRange(good, notLinked, recycled, inactiveDoc);
            foreach (var d in new[] { good, notLinked, recycled, inactiveDoc })
            {
                _dbContext.TransferDocuments.Add(new TransferDocument
                {
                    TransferDocumentId = Guid.NewGuid(),
                    TransferRequestId = requestId,
                    DocumentId = d.DocumentId,
                    IsActive = true
                });
            }
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetDocumentIdsAsync(requestId);

            Assert.Equal(new[] { good.DocumentId }, result.ToArray());
        }

        [Fact]
        public async Task AddDocumentsAsync_StagesDocumentsUntilSaved()
        {
            var docs = new[]
            {
                new TransferDocument { TransferDocumentId = Guid.NewGuid(), TransferRequestId = Guid.NewGuid(), DocumentId = Guid.NewGuid(), IsActive = true }
            };

            await _repository.AddDocumentsAsync(docs);
            await _repository.SaveChangesAsync();

            Assert.Equal(1, await _dbContext.TransferDocuments.CountAsync());
        }

        [Fact]
        public async Task InsertDocumentsIntoTransferDocumentTableAsync_NullOrEmpty_ReturnsTrue()
        {
            Assert.True(await _repository.InsertDocumentsIntoTransferDocumentTableAsync(null!, Guid.NewGuid(), Guid.NewGuid()));
            Assert.True(await _repository.InsertDocumentsIntoTransferDocumentTableAsync(new List<Guid>(), Guid.NewGuid(), Guid.NewGuid()));
        }

        [Fact]
        public async Task InsertDocumentsIntoTransferDocumentTableAsync_NewDocuments_LinksThem()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var transfer = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement);
            var doc1 = NewDocument();
            var doc2 = NewDocument();
            _dbContext.Transfers.Add(transfer);
            _dbContext.Documents.AddRange(doc1, doc2);
            await _dbContext.SaveChangesAsync();
            var userId = Guid.NewGuid();

            var result = await _repository.InsertDocumentsIntoTransferDocumentTableAsync(
                new List<Guid> { doc1.DocumentId, doc2.DocumentId, doc1.DocumentId }, request.TransferRequestId, userId);

            Assert.True(result);
            Assert.Equal(2, await _dbContext.TransferDocuments.CountAsync(x => x.TransferRequestId == request.TransferRequestId));
            var saved = await _dbContext.Documents.AsNoTracking().FirstAsync(d => d.DocumentId == doc1.DocumentId);
            Assert.True(saved.IsModuleLinked);
            Assert.Equal(userId, saved.ModifiedBy);
        }

        [Fact]
        public async Task InsertDocumentsIntoTransferDocumentTableAsync_AlreadyLinked_ReturnsFalseWithoutChanges()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var transfer = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement);
            var doc = NewDocument();
            _dbContext.Transfers.Add(transfer);
            _dbContext.Documents.Add(doc);
            _dbContext.TransferDocuments.Add(new TransferDocument
            {
                TransferDocumentId = Guid.NewGuid(),
                TransferRequestId = request.TransferRequestId,
                DocumentId = doc.DocumentId,
                IsActive = true
            });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.InsertDocumentsIntoTransferDocumentTableAsync(
                new List<Guid> { doc.DocumentId }, request.TransferRequestId, Guid.NewGuid());

            Assert.False(result);
            Assert.Equal(1, await _dbContext.TransferDocuments.CountAsync());
        }

        [Fact]
        public async Task InsertDocumentsIntoTransferDocumentTableAsync_UnknownDocument_Throws()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            _dbContext.Transfers.Add(NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement));
            await _dbContext.SaveChangesAsync();

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _repository.InsertDocumentsIntoTransferDocumentTableAsync(
                    new List<Guid> { Guid.NewGuid() }, request.TransferRequestId, Guid.NewGuid()));
        }

        [Fact]
        public async Task ReplaceBankTransferDocumentsAsync_EmptyList_ReturnsEmpty()
        {
            var result = await _repository.ReplaceBankTransferDocumentsAsync(Guid.NewGuid(), new List<Guid>(), Guid.NewGuid());

            Assert.Empty(result);
        }

        [Fact]
        public async Task ReplaceBankTransferDocumentsAsync_SwapsLinkedDocuments()
        {
            var entityId = Guid.NewGuid();
            var request = NewRequest(initiatingEntityId: entityId);
            var transfer = NewTransfer(request.TransferRequestId, entityId, TransferCodes.TransferLegCode.Disbursement);
            var removedDoc = NewDocument(); removedDoc.IsModuleLinked = true;
            var keptDoc = NewDocument(); keptDoc.IsModuleLinked = true;
            var newDoc = NewDocument();
            _dbContext.Transfers.Add(transfer);
            _dbContext.Documents.AddRange(removedDoc, keptDoc, newDoc);
            _dbContext.TransferDocuments.AddRange(
                new TransferDocument { TransferDocumentId = Guid.NewGuid(), TransferRequestId = request.TransferRequestId, DocumentId = removedDoc.DocumentId, IsActive = true },
                new TransferDocument { TransferDocumentId = Guid.NewGuid(), TransferRequestId = request.TransferRequestId, DocumentId = keptDoc.DocumentId, IsActive = true });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.ReplaceBankTransferDocumentsAsync(
                request.TransferRequestId,
                new List<Guid> { keptDoc.DocumentId, newDoc.DocumentId, Guid.Empty, newDoc.DocumentId },
                Guid.NewGuid());

            Assert.Equal(new[] { keptDoc.DocumentId, newDoc.DocumentId }, result.ToArray());

            // The repository does not await its final SaveChangesAsync; make sure it has flushed before asserting.
            await _dbContext.SaveChangesAsync();

            var links = await _dbContext.TransferDocuments.AsNoTracking()
                .Where(x => x.TransferRequestId == request.TransferRequestId).Select(x => x.DocumentId).ToListAsync();
            Assert.Equal(2, links.Count);
            Assert.DoesNotContain(removedDoc.DocumentId, links);
            Assert.False((await _dbContext.Documents.AsNoTracking().SingleAsync(d => d.DocumentId == removedDoc.DocumentId)).IsModuleLinked);
            Assert.True((await _dbContext.Documents.AsNoTracking().SingleAsync(d => d.DocumentId == newDoc.DocumentId)).IsModuleLinked);
        }

        #endregion

        #region GetBankTransferTagsAsync

        [Fact]
        public async Task GetBankTransferTagsAsync_NoTransfersForEntity_ReturnsEmpty()
        {
            var result = await _repository.GetBankTransferTagsAsync(new BankTransferTagRequestDto { EntityId = Guid.NewGuid() });

            Assert.NotNull(result);
            Assert.Empty(result!);
        }

        [Fact]
        public async Task GetBankTransferTagsAsync_ReturnsDistinctTagsWithColorAndUnknownFallbacks()
        {
            var entityId = Guid.NewGuid();
            var requestId = Guid.NewGuid();
            var tagId = Guid.NewGuid();
            var orphanTagId = Guid.NewGuid();
            var colorId = Guid.NewGuid();

            _dbContext.Transfers.Add(NewTransfer(requestId, entityId, TransferCodes.TransferLegCode.Disbursement));
            _dbContext.Tags.Add(new Tag { TagId = tagId, Name = "Urgent", ColorId = colorId });
            _dbContext.ColorCharts.Add(new ColorChart { ColorId = colorId, HexCode = "#FF0000" });
            _dbContext.TagTransfers.AddRange(
                new TagTransfer { TransferId = requestId, TagId = tagId, IsActive = true },
                new TagTransfer { TransferId = requestId, TagId = tagId, IsActive = true },
                new TagTransfer { TransferId = requestId, TagId = orphanTagId, IsActive = true },
                new TagTransfer { TransferId = requestId, TagId = Guid.NewGuid(), IsActive = false });
            await _dbContext.SaveChangesAsync();

            var result = await _repository.GetBankTransferTagsAsync(new BankTransferTagRequestDto { EntityId = entityId });

            Assert.NotNull(result);
            Assert.Equal(2, result!.Count);
            var known = result.Single(r => r.TagId == tagId);
            Assert.Equal("Urgent", known.TagName);
            Assert.Equal("#FF0000", known.HexCode);
            var orphan = result.Single(r => r.TagId == orphanTagId);
            Assert.Equal(Constants.Unknown, orphan.TagName);
            Assert.Equal("#000000", orphan.HexCode);
        }

        #endregion

        #region GetSuggestionsAsync

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task GetSuggestionsAsync_BlankSearchText_ReturnsEmpty(string? searchText)
        {
            var result = await _repository.GetSuggestionsAsync(
                new TransferSuggestionRequest { SearchText = searchText!, EntityId = Guid.NewGuid(), Filter = null });

            Assert.Empty(result);
        }

        private async Task<Guid> SeedSuggestionGraphAsync(string method = "ACH", decimal amount = 100m, string status = "DRAFT")
        {
            var entityId = Guid.NewGuid();
            var request = new TransferRequest
            {
                TransferRequestId = Guid.NewGuid(),
                TransferNumber = 123,
                DraftNumber = 1,
                TransferMethodCode = method,
                TransferAmount = amount,
                TransferRefId = "D01",
                TransferStatusCode = status,
                TransferTypeCode = "WIRE",
                IsActive = true
            };
            _dbContext.Entities.Add(new Entity { Id = entityId, EntityName = "Acme Corp" });
            _dbContext.TransferRequests.Add(request);
            _dbContext.TransferTypes.Add(new TransferType
            {
                TransferTypeCode = "WIRE",
                TransferTypeName = "Wire Transfer",
                IsActive = true,
                CreatedDate = DateTime.UtcNow,
                CreatedBy = Guid.NewGuid()
            });
            var transfer = NewTransfer(request.TransferRequestId, entityId, "DISBURSEMENT");
            transfer.TransferTypeCode = "WIRE";
            transfer.ProcessingStatusCode = "COMPLETED";
            transfer.TransferDate = DateTime.UtcNow;
            transfer.CreatedDate = DateTime.UtcNow;
            _dbContext.Transfers.Add(transfer);
            await _dbContext.SaveChangesAsync();
            return entityId;
        }

        [Fact]
        public async Task GetSuggestionsAsync_AllFiltersMatching_ReturnsSuggestion()
        {
            var entityId = await SeedSuggestionGraphAsync();

            var result = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new()
                {
                    Methods = ["ACH"],
                    Types = ["Wire Transfer"],
                    Statuses = ["DRAFT"],
                    AmountFrom = 50m,
                    AmountTo = 150m,
                    DateFrom = DateTime.UtcNow.AddDays(-1),
                    DateTo = DateTime.UtcNow.AddDays(1)
                }
            });

            Assert.Contains("Acme Corp", result);
        }

        [Fact]
        public async Task GetSuggestionsAsync_MethodFilterExcludes_ReturnsEmpty()
        {
            var entityId = await SeedSuggestionGraphAsync();

            var result = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { Methods = ["WIRE"] }
            });

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetSuggestionsAsync_TypeFilterExcludes_ReturnsEmpty()
        {
            var entityId = await SeedSuggestionGraphAsync();

            var result = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { Types = ["Other Type"] }
            });

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetSuggestionsAsync_StatusFilterExcludes_ReturnsEmpty()
        {
            var entityId = await SeedSuggestionGraphAsync();

            var result = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { Statuses = ["CANCELLED"] }
            });

            Assert.Empty(result);
        }

        [Fact]
        public async Task GetSuggestionsAsync_AmountFilterExcludes_ReturnsEmpty()
        {
            var entityId = await SeedSuggestionGraphAsync(amount: 100m);

            var tooLow = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { AmountFrom = 500m }
            });
            var tooHigh = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { AmountTo = 10m }
            });

            Assert.Empty(tooLow);
            Assert.Empty(tooHigh);
        }

        [Fact]
        public async Task GetSuggestionsAsync_DateFilterExcludes_ReturnsEmpty()
        {
            var entityId = await SeedSuggestionGraphAsync();

            var tooEarly = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { DateFrom = DateTime.UtcNow.AddDays(5) }
            });
            var tooLate = await _repository.GetSuggestionsAsync(new TransferSuggestionRequest
            {
                SearchText = "Acme",
                EntityId = entityId,
                Filter = new() { DateTo = DateTime.UtcNow.AddDays(-5) }
            });

            Assert.Empty(tooEarly);
            Assert.Empty(tooLate);
        }

        #endregion
    }
}
