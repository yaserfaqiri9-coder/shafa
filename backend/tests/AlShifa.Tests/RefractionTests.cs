using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AlShifa.Api.Features.Auth;
using AlShifa.Api.Features.Clinical;
using AlShifa.Api.Features.Finance;
using AlShifa.Api.Features.Patients;
using AlShifa.Api.Features.Visits;

namespace AlShifa.Tests;

[Collection("api")]
public sealed class RefractionTests(ApiFixture api) : PhysicalQueueTestScope(api)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    private static string Name(string prefix) => $"{prefix}_{Guid.NewGuid():N}"[..20];

    private async Task<HttpClient> UserAsync(params string[] roles)
    {
        var name = Name("u");
        await api.CreateUserAsync(name, roles);
        return await api.LoginAsync(name);
    }

    private static HttpRequestMessage Post(string url, object body)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        message.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        return message;
    }

    private static async Task<T> Read<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, text);
        return JsonSerializer.Deserialize<T>(text, JsonOptions)!;
    }

    private async Task<(PatientDto Patient, VisitRow Visit)> NewVisitAsync()
    {
        var reception = await UserAsync(Roles.Receptionist);
        var patient = await Read<PatientDto>(await reception.SendAsync(Post("/api/patients",
            new SavePatientRequest(Name("احمد"), null, "محمود", Gender.Male, null, 30, null, null))), HttpStatusCode.Created);
        var departments = await reception.GetFromJsonAsync<List<LookupItem>>("/api/lookups/departments");
        var visit = await Read<VisitRow>(await reception.SendAsync(Post("/api/visits",
            new CreateVisitRequest(patient.Id, (int)departments!.Single(d => d.Name == "معاینه ابتدایی").Id, null, null, null, await api.PaidTokenAsync()))), HttpStatusCode.Created);
        await api.CompleteVisionAsync(visit.Id);
        await RequestAndPayAsync(patient.Id, visit.Id);
        return (patient, visit);
    }

    private async Task RequestAndPayAsync(long patientId, long visitId)
    {
        var doctor = await UserAsync(Roles.Doctor);
        var tests = await doctor.GetFromJsonAsync<JsonElement>("/api/diagnostic-tests");
        var testId = tests.EnumerateArray().First(t => t.GetProperty("kind").GetString() == "Refraction" && t.GetProperty("serviceId").ValueKind == JsonValueKind.Number).GetProperty("id").GetInt32();
        var order = await Read<DiagnosticOrderDto>(await doctor.SendAsync(Post("/api/diagnostic-orders", new CreateDiagnosticOrderRequest(patientId, visitId, testId, null))), HttpStatusCode.Created);

        var fileNumber = (await doctor.GetFromJsonAsync<PatientDto>($"/api/patients/{patientId}", JsonOptions))!.FileNumber;
        await api.PayFeeAsync(await api.OrderFeeAsync(fileNumber, order.TestName));
    }

    private static SaveRefractionRequest Sample(int? version, string finalSphere = "-1.25") => new(
        "6/18", "6/9", "6/9", null, null, null, "62", "K1 43.25", "یادداشت",
        [
            new RefractionLineInput(RefractionStage.Auto, EyeSide.OD, "-1.50", "-0.50", "90", null, null),
            new RefractionLineInput(RefractionStage.Final, EyeSide.OD, finalSphere, "-0.50", "90", "+1.00", "6/6"),
            new RefractionLineInput(RefractionStage.Final, EyeSide.OS, null, null, null, null, null),
        ],
        version);

    [Fact]
    public async Task Stages_stay_separate_and_blank_rows_are_not_stored()
    {
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);

        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        Assert.Equal(2, saved.Lines.Count);
        Assert.Equal("-1.50", saved.Lines.Single(l => l.Stage == "Auto" && l.Eye == "OD").Sphere);
        Assert.Equal("-1.25", saved.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Sphere);
        Assert.Equal("6/6", saved.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Va);
        Assert.DoesNotContain(saved.Lines, l => l.Eye == "OS");

        var doctor = await UserAsync(Roles.Doctor);
        var read = await Read<RefractionDto>(await doctor.GetAsync($"/api/refractions/{visit.Id}"));
        Assert.Equal("62", read.Pd);
        var list = await doctor.GetFromJsonAsync<List<RefractionDto>>($"/api/clinical/patients/{patient.Id}/refractions", JsonOptions);
        Assert.Contains(list!, r => r.VisitId == visit.Id);
    }

    [Fact]
    public async Task Saving_again_replaces_lines_and_a_stale_version_is_refused()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var first = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        var second = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(first.Version, "-2.00")));
        Assert.Equal("-2.00", second.Lines.Single(l => l.Stage == "Final" && l.Eye == "OD").Sphere);
        Assert.Equal(2, second.Lines.Count);

        var stale = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(first.Version, "-3.00"));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Final_values_reach_the_optical_desk_and_a_correction_follows_them()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var optical = await UserAsync(Roles.Optical);
        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        var complete = await triage.PostAsJsonAsync($"/api/refractions/{visit.Id}/complete", new CompleteRefractionRequest(ReturnToDoctor: false, SendToOptical: true));
        Assert.Equal(HttpStatusCode.NoContent, complete.StatusCode);

        // Straight to the optical desk: the doctor's queue does not get the patient back.
        var after = await triage.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions);
        Assert.NotEqual(nameof(VisitStatus.ReadyForDoctor), after!.Status);

        var rx = (await optical.GetFromJsonAsync<List<OpticalRxDto>>("/api/optical-prescriptions", JsonOptions))!.Single(p => p.VisitId == visit.Id);
        Assert.True(rx.FromRefraction);
        Assert.Equal(("-1.25", "-0.50", "90", "+1.00"), (rx.SphereOd, rx.CylinderOd, rx.AxisOd, rx.AddOd));
        Assert.Null(rx.SphereOs);
        Assert.Equal("62", rx.Pd);

        var record = await triage.GetFromJsonAsync<RefractionDto>($"/api/refractions/{visit.Id}", JsonOptions);
        Assert.Equal(rx.RxNumber, record!.OpticalRxNumber);

        var again = await triage.PostAsync($"/api/refractions/{visit.Id}/send-to-optical", null);
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);

        // No glasses ordered yet, so the corrected final values replace the copy's.
        var amended = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(record.Version, "-2.00") with { AmendReason = "اشتباه در ثبت" });
        Assert.Equal(HttpStatusCode.OK, amended.StatusCode);
        var corrected = (await optical.GetFromJsonAsync<List<OpticalRxDto>>("/api/optical-prescriptions", JsonOptions))!.Single(p => p.VisitId == visit.Id);
        Assert.Equal("-2.00", corrected.SphereOd);
    }

    [Fact]
    public async Task Sending_without_final_values_is_refused_and_completes_nothing()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null) with
        {
            Lines = [new RefractionLineInput(RefractionStage.Auto, EyeSide.OD, "-1.50", null, null, null, null)],
        }));

        var refused = await triage.PostAsJsonAsync($"/api/refractions/{visit.Id}/complete", new CompleteRefractionRequest(SendToOptical: true));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);

        var record = await triage.GetFromJsonAsync<RefractionDto>($"/api/refractions/{visit.Id}", JsonOptions);
        Assert.Null(record!.CompletedAt);
        Assert.Null(record.OpticalRxNumber);
    }

    [Fact]
    public async Task Completing_locks_the_record_and_returns_the_patient_to_the_doctor()
    {
        var (_, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null)));

        Assert.Equal(HttpStatusCode.NoContent, (await triage.PostAsync($"/api/refractions/{visit.Id}/complete", null)).StatusCode);

        var locked = await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(saved.Version + 1));
        Assert.Equal(HttpStatusCode.Conflict, locked.StatusCode);

        var after = await triage.GetFromJsonAsync<VisitRow>($"/api/visits/{visit.Id}", JsonOptions);
        Assert.Equal(nameof(VisitStatus.ReadyForDoctor), after!.Status);
    }

    [Fact]
    public async Task Only_pre_exam_roles_can_write_and_reception_cannot_read()
    {
        var (_, visit) = await NewVisitAsync();
        var doctor = await UserAsync(Roles.Doctor);
        var reception = await UserAsync(Roles.Receptionist);

        Assert.Equal(HttpStatusCode.Forbidden, (await doctor.PutAsJsonAsync($"/api/refractions/{visit.Id}", Sample(null))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reception.GetAsync($"/api/refractions/{visit.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await doctor.GetAsync($"/api/refractions/{visit.Id}")).StatusCode);
    }

    [Fact]
    public async Task Paper_prescription_roundtrips_all_four_rows_and_independent_lenses()
    {
        var (patient, visit) = await NewVisitAsync();
        var triage = await UserAsync(Roles.Triage);
        const string lenses = "Lens: ph-Brown, Ph-Gray, White, Distance, Near, Bifocal, Progressive, Antireflex, Plastic";
        var input = Sample(null) with
        {
            UnaidedVaOd = "6/12.5", UnaidedVaOs = "+0.75", Pd = "62.75",
            Measurements = "K1 43.25\n" + lenses,
            Lines = [
                new(RefractionStage.Subjective, EyeSide.OD, "+1.125", "-0.375", "90.5", null, "6/7.5"),
                new(RefractionStage.Subjective, EyeSide.OS, "-2.25", "+0.50", "-1.5", null, "N6"),
                new(RefractionStage.Final, EyeSide.OD, "+1.00", "-0.25", "180", "+2.125", "6/6"),
                new(RefractionStage.Final, EyeSide.OS, "-2.00", "+0.75", "0", "+2.125", "6/9"),
            ],
        };
        var saved = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", input));
        var doctor = await UserAsync(Roles.Doctor);
        var loaded = await Read<RefractionDto>(await doctor.GetAsync($"/api/refractions/{visit.Id}"));
        Assert.Equal(JsonSerializer.Serialize(saved, JsonOptions), JsonSerializer.Serialize(loaded, JsonOptions));
        Assert.Equal(4, loaded.Lines.Count);
        foreach (var line in input.Lines!)
        {
            var actual = loaded.Lines.Single(l => l.Stage == line.Stage.ToString() && l.Eye == line.Eye.ToString());
            Assert.Equal((line.Sphere, line.Cylinder, line.Axis, line.Add, line.Va),
                (actual.Sphere, actual.Cylinder, actual.Axis, actual.Add, actual.Va));
        }
        Assert.Equal(("6/12.5", "+0.75", "62.75", input.Measurements),
            (loaded.UnaidedVaOd, loaded.UnaidedVaOs, loaded.Pd, loaded.Measurements));
        var edited = await Read<RefractionDto>(await triage.PutAsJsonAsync($"/api/refractions/{visit.Id}", input with
        {
            Version = loaded.Version, Measurements = "K1 43.25\nLens: Near, Plastic", Pd = "-0.5",
        }));
        Assert.Equal("K1 43.25\nLens: Near, Plastic", edited.Measurements);
        Assert.Equal("-0.5", edited.Pd);
        var history = await doctor.GetFromJsonAsync<List<RefractionDto>>($"/api/clinical/patients/{patient.Id}/refractions", JsonOptions);
        Assert.Equal(edited.Measurements, history!.Single(r => r.VisitId == visit.Id).Measurements);
    }
}
